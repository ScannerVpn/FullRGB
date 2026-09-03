using System.IO;
using System.Net.Sockets;

namespace FullRGB.SDK;

/// <summary>One rendered zone, ready to be written.</summary>
public readonly struct ZonePaint
{
    public readonly int ZoneIndex;
    public readonly byte[] Rgb;
    public ZonePaint(int zoneIndex, byte[] rgb) { ZoneIndex = zoneIndex; Rgb = rgb; }
}

/// <summary>
/// A dedicated SDK connection that streams LED frames to ONE device, with its own writer thread,
/// a single-slot mailbox (latest frame wins) and per-frame completion acks.
///
/// WHY IT LOOKS LIKE THIS — every claim measured on this rig, probes kept in <c>_probe/</c>:
///
/// 1. <c>socket_per_device.py</c>: the OpenRGB server handles each CLIENT connection serially, so
///    sharing one socket lets a slow device block a fast one (write stalls to 411 ms, engine stuck
///    near 20 fps). One connection per device removed the cross-talk.
///
/// 2. <c>device_capacity.py</c>: per-zone write capacity differs wildly per device — the ASUS board
///    takes ~6.4 ms per zone write, the Corsair Commander Core ~49 ms. Pushing 30 fps at the
///    Corsair overruns it and a single <c>send()</c> then blocks up to 17.9 SECONDS.
///
/// 3. <c>poll_backpressure.py</c>: select-for-write does NOT predict that stall (still blocked
///    12.6 s) — the backlog sits in the server, not the kernel send buffer.
///
/// 4. <c>ack_flowcontrol.py</c>: a <c>REQUEST_PROTOCOL_VERSION</c> round-trip IS an exact
///    completion ack, because the server processes one connection's packets in order. With one
///    frame in flight the worst case collapsed from 17.9 s to 343 ms (the Corsair's true frame
///    cost) and rates became stable: ASUS 42.6 fps, Corsair 5.2 fps.
///
/// 5. <c>mailbox_writer.py</c>: rendering into a one-slot mailbox keeps the render loop at a
///    steady 30 fps regardless of device speed; stale frames are simply dropped.
///
/// Discovery, zone resizing and mode changes stay on the shared <see cref="OpenRgbClient"/>, so
/// there is exactly one owner of request/response traffic and no protocol interleaving.
/// </summary>
public sealed class DeviceChannel : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _clientName;
    private readonly int _protocolVersion;

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private byte[] _buf = new byte[16 + 4096];
    private readonly byte[] _ackHeader = new byte[16];

    private Thread? _writer;
    private readonly object _mailbox = new();
    private ZonePaint[]? _pending;
    private volatile bool _stop;
    private long _delivered, _dropped;
    private volatile Exception? _fault;
    private double _lastFrameMs;

    /// <summary>
    /// True when the server answered the handshake, i.e. request/response works and can be used
    /// as a per-frame completion ack. A silent (very old) server would make every ack read block
    /// until the receive timeout, so in that case the writer just writes without acking.
    /// </summary>
    private bool _ackSupported;

    public int DeviceIndex { get; }
    public bool IsOpen => _stream is not null && _tcp is { Connected: true } && _fault is null;
    /// <summary>Non-null once the writer thread hit an IO error; the owner must reopen the channel.</summary>
    public Exception? Fault => _fault;
    /// <summary>Frames acknowledged by the server (i.e. actually applied).</summary>
    public long Delivered => Interlocked.Read(ref _delivered);
    /// <summary>Frames superseded before the writer could send them (expected on slow devices).</summary>
    public long Dropped => Interlocked.Read(ref _dropped);
    /// <summary>
    /// Measured cost of the last complete frame (all zone writes + ack), in ms.
    /// This is the device's real speed and is what the render loop paces itself to.
    /// </summary>
    public double LastFrameMs => Volatile.Read(ref _lastFrameMs);

    public DeviceChannel(int deviceIndex, string host, int port, string clientName, int protocolVersion)
    {
        DeviceIndex = deviceIndex;
        _host = host;
        _port = port;
        _clientName = clientName;
        _protocolVersion = protocolVersion;
    }

    /// <summary>Opens the socket, performs the handshake and starts the writer thread.</summary>
    public void Open()
    {
        Close();
        _fault = null;
        _stop = false;
        Volatile.Write(ref _lastFrameMs, 0);

        var tcp = new TcpClient();
        if (!tcp.ConnectAsync(_host, _port).Wait(TimeSpan.FromSeconds(5)))
        {
            try { tcp.Dispose(); } catch { }
            throw new SocketException((int)SocketError.TimedOut);
        }
        tcp.NoDelay = true;
        // No SendTimeout: with ack flow control at most one frame is in flight, and a slow device
        // legitimately keeps the WRITER thread busy for a few hundred ms. The render thread never
        // waits on this socket, so a long write cannot affect the animation's timing.
        tcp.SendTimeout = 0;
        tcp.ReceiveTimeout = 20000;
        _tcp = tcp;
        _stream = tcp.GetStream();

        // Handshake: announce protocol version, DRAIN the reply (otherwise it would be mistaken
        // for the first frame ack), then announce a distinct client name.
        Write(0, Pkt.REQUEST_PROTOCOL_VERSION, BitConverter.GetBytes((uint)_protocolVersion), 4);
        _ackSupported = false;
        try
        {
            _tcp.ReceiveTimeout = 1500;
            ReadPacket();
            _ackSupported = true;   // the server answers requests, so acks are usable
        }
        catch (Exception e) when (e is IOException or SocketException)
        {
            // Older servers stay silent; then there is nothing to drain and acks are unavailable.
        }
        finally
        {
            _tcp.ReceiveTimeout = 20000;
        }

        var name = System.Text.Encoding.UTF8.GetBytes($"{_clientName}-dev{DeviceIndex}\0");
        Write(0, Pkt.SET_CLIENT_NAME, name, name.Length);

        _writer = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = $"FullRGB-write-dev{DeviceIndex}",
        };
        _writer.Start();
    }

    /// <summary>
    /// Hands a complete frame to the writer. NEVER blocks: if the previous frame has not been
    /// written yet it is dropped, because a newer frame makes it irrelevant.
    /// </summary>
    public void Submit(ZonePaint[] frame)
    {
        lock (_mailbox)
        {
            if (_pending is not null) Interlocked.Increment(ref _dropped);
            _pending = frame;
            Monitor.Pulse(_mailbox);
        }
    }

    private void WriterLoop()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!_stop)
        {
            ZonePaint[]? frame;
            lock (_mailbox)
            {
                while (_pending is null && !_stop) Monitor.Wait(_mailbox, 200);
                frame = _pending;
                _pending = null;
            }
            if (_stop) return;
            if (frame is null) continue;

            try
            {
                double t0 = clock.Elapsed.TotalMilliseconds;
                foreach (var paint in frame) WriteZone(paint.ZoneIndex, paint.Rgb);

                // Completion ack: the server answers this only after the writes above have been
                // processed, so exactly one frame is ever in flight and no backlog can build.
                if (_ackSupported)
                {
                    Write(0, Pkt.REQUEST_PROTOCOL_VERSION, BitConverter.GetBytes((uint)_protocolVersion), 4);
                    ReadPacket();
                }

                Volatile.Write(ref _lastFrameMs, clock.Elapsed.TotalMilliseconds - t0);
                Interlocked.Increment(ref _delivered);
            }
            catch (Exception e)
            {
                _fault = e;      // the owning loop reopens the channel
                return;
            }
        }
    }

    /// <summary>
    /// Payload: u32 total_size + i32 zone_index + u16 led_count + count*(R,G,B,pad).
    /// Header+payload go out as ONE write: two writes with NoDelay produce two TCP segments.
    /// </summary>
    private void WriteZone(int zoneIndex, byte[] rgb)
    {
        int leds = rgb.Length / 3;
        if (leds <= 0) return;
        int size = 4 + 4 + 2 + leds * 4;
        int total = 16 + size;
        if (_buf.Length < total) _buf = new byte[Math.Max(total, _buf.Length * 2)];

        _buf[0] = (byte)'O'; _buf[1] = (byte)'R'; _buf[2] = (byte)'G'; _buf[3] = (byte)'B';
        BitConverter.TryWriteBytes(_buf.AsSpan(4), (uint)DeviceIndex);
        BitConverter.TryWriteBytes(_buf.AsSpan(8), Pkt.UPDATE_ZONE_LEDS);
        BitConverter.TryWriteBytes(_buf.AsSpan(12), (uint)size);
        BitConverter.TryWriteBytes(_buf.AsSpan(16), size);
        BitConverter.TryWriteBytes(_buf.AsSpan(20), zoneIndex);
        BitConverter.TryWriteBytes(_buf.AsSpan(24), (ushort)leds);
        for (int i = 0; i < leds; i++)
        {
            int o = 26 + i * 4;
            _buf[o] = rgb[i * 3];
            _buf[o + 1] = rgb[i * 3 + 1];
            _buf[o + 2] = rgb[i * 3 + 2];
            _buf[o + 3] = 0;
        }
        (_stream ?? throw new IOException("channel closed")).Write(_buf, 0, total);
    }

    private void Write(int deviceId, uint packetType, byte[] payload, int length)
    {
        int total = 16 + length;
        if (_buf.Length < total) _buf = new byte[Math.Max(total, _buf.Length * 2)];
        _buf[0] = (byte)'O'; _buf[1] = (byte)'R'; _buf[2] = (byte)'G'; _buf[3] = (byte)'B';
        BitConverter.TryWriteBytes(_buf.AsSpan(4), (uint)deviceId);
        BitConverter.TryWriteBytes(_buf.AsSpan(8), packetType);
        BitConverter.TryWriteBytes(_buf.AsSpan(12), (uint)length);
        if (length > 0) Buffer.BlockCopy(payload, 0, _buf, 16, length);
        (_stream ?? throw new IOException("channel closed")).Write(_buf, 0, total);
    }

    /// <summary>Reads and discards one SDK packet (used for the handshake reply and frame acks).</summary>
    private void ReadPacket()
    {
        var stream = _stream ?? throw new IOException("channel closed");
        ReadExact(stream, _ackHeader, 16);
        if (_ackHeader[0] != (byte)'O' || _ackHeader[1] != (byte)'R' ||
            _ackHeader[2] != (byte)'G' || _ackHeader[3] != (byte)'B')
            throw new IOException("invalid SDK packet magic on device channel");
        uint size = BitConverter.ToUInt32(_ackHeader, 12);
        if (size > OpenRgbClient.MaxPayloadSize) throw new IOException($"ack payload too large: {size}");
        if (size == 0) return;
        var scratch = size <= (uint)_buf.Length ? _buf : new byte[size];
        ReadExact(stream, scratch, (int)size);
    }

    private static void ReadExact(NetworkStream s, byte[] buf, int n)
    {
        int off = 0;
        while (off < n)
        {
            int read = s.Read(buf, off, n - off);
            if (read <= 0) throw new IOException("SDK connection closed");
            off += read;
        }
    }

    public void Close()
    {
        _stop = true;
        lock (_mailbox)
        {
            _pending = null;
            Monitor.PulseAll(_mailbox);
        }
        // A writer parked inside a multi-hundred-ms device write cannot be interrupted politely;
        // closing the socket makes it throw, and it is a background thread either way.
        try { _stream?.Close(); } catch { }
        try { _tcp?.Close(); } catch { }
        try { _writer?.Join(500); } catch { }
        _writer = null;
        _stream = null;
        _tcp = null;
    }

    public void Dispose() => Close();
}
