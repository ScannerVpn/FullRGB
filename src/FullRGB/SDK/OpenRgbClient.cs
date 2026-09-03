using System.Diagnostics;
using System.IO;
using System.Net.Sockets;

namespace FullRGB.SDK;

/// <summary>
/// OpenRGB SDK protocol client. Wire format (all little-endian):
/// header = magic "ORGB" (4) + device_id u32 + packet_type u32 + payload_size u32 (16 bytes total),
/// followed by payload_size bytes. Header device_id selects the target controller for
/// UPDATE_* / SET_CUSTOM_MODE packets; discovery packets use 0.
/// </summary>
public sealed class OpenRgbClient : IDisposable
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    /// <summary>
    /// ONE lock for every socket operation. Send and Request used to take different locks, so a
    /// render-loop write could slip between a request's write and its read; with a single lock
    /// the request/response pairing can never be interleaved.
    /// </summary>
    private readonly object _io = new();
    public const uint MaxPayloadSize = 64 * 1024 * 1024;
    public int ProtocolVersion { get; private set; }
    public List<RgbController> Controllers { get; private set; } = new();
    public bool Connected { get; private set; }

    /// <summary>Raised when a write fails, i.e. the server went away. Consumers should reconnect.</summary>
    public event Action<Exception>? ConnectionLost;

    private string _host = "127.0.0.1";
    private int _port = 6742;
    private string _clientName = "FullRGB";

    // reusable frame buffers: at 30 fps × 12 zones the old code allocated ~10 MB/minute
    private byte[] _payloadBuf = new byte[4096];
    private byte[] _frameBuf = new byte[4096 + 16];

    /// <summary>Endpoint of the live connection, so per-device channels can reuse it.</summary>
    public string Host => _host;
    public int Port => _port;
    public string ClientName => _clientName;

    /// <summary>True while the socket is usable. Cheap: no traffic generated.</summary>
    public bool IsAlive => Connected && _tcp is { Connected: true };

    public void Connect(string host, int port, string clientName = "FullRGB", CancellationToken ct = default)
    {
        _host = host; _port = port; _clientName = clientName;
        Disconnect();
        var tcp = new TcpClient();
        // Bounded connect without .Wait() (which wraps errors in AggregateException and can
        // deadlock on a UI SynchronizationContext): race the connect against a 5s timeout.
        try
        {
            var connectTask = tcp.ConnectAsync(host, port);
            if (!connectTask.Wait(TimeSpan.FromSeconds(5), ct))
            {
                try { tcp.Dispose(); } catch { }
                throw new SocketException((int)SocketError.TimedOut);
            }
            if (connectTask.IsFaulted) throw connectTask.Exception?.InnerException ?? new SocketException();
        }
        catch (OperationCanceledException) { try { tcp.Dispose(); } catch { } throw; }
        tcp.ReceiveTimeout = 15000;
        tcp.SendTimeout = 5000;
        tcp.NoDelay = true;
        _tcp = tcp;
        _stream = tcp.GetStream();

        // 1) protocol version handshake; older servers stay silent -> fall back to v0.
        // The whole Send+Read exchange holds _io so a concurrent render write can never
        // interleave between our write and its reply and desync the stream.
        ProtocolVersion = 4;
        lock (_io)
        {
            SendLocked(0, Pkt.REQUEST_PROTOCOL_VERSION, BitConverter.GetBytes((uint)ProtocolVersion), 4);
            try
            {
                // short receive window: silent servers must not stall the connect for 15s
                _tcp!.ReceiveTimeout = 1500;
                var (_, reply) = ReadPacket(ct);
                if (reply.Length >= 4)
                {
                    uint serverMax = BitConverter.ToUInt32(reply, 0);
                    ProtocolVersion = (int)Math.Min(serverMax, 4u);
                }
            }
            catch (Exception e) when (e is SocketException or IOException)
            {
                ProtocolVersion = 0;
            }
            finally
            {
                try { _tcp!.ReceiveTimeout = 15000; } catch { }
            }

            // 2) announce client name (null-terminated) — no reply expected from the server
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(clientName + "\0");
            SendLocked(0, Pkt.SET_CLIENT_NAME, nameBytes, nameBytes.Length);
        }

        // 3) enumerate controllers
        Connected = true;
        RefreshControllers(ct);
    }

    /// <summary>Reconnects to the same endpoint and restores paintable state. Throws on failure.</summary>
    public void Reconnect(CancellationToken ct = default)
    {
        Connect(_host, _port, _clientName, ct);
        ExpandAllZones(ct);
        EnsureDirectMode();
    }

    /// <summary>Re-reads every controller (after zone resizes or a device list change).</summary>
    public void RefreshControllers(CancellationToken ct = default)
    {
        var (_, countPayload) = Request(0, Pkt.REQUEST_CONTROLLER_COUNT, Array.Empty<byte>(), ct);
        uint count = countPayload.Length >= 4 ? BitConverter.ToUInt32(countPayload, 0) : 0u;
        if (count > 512) count = 512;   // sanity: a bad reply must not spin for 4 billion devices
        var list = new List<RgbController>((int)count);
        for (uint i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (_, data) = Request((int)i, Pkt.REQUEST_CONTROLLER_DATA, BitConverter.GetBytes((uint)ProtocolVersion), ct);
            list.Add(DeviceParser.Parse((int)i, data));
        }
        Controllers = list;
    }

    /// <summary>
    /// Grows a zone to a given LED count. ARGB headers report 0 LEDs until resized,
    /// which is why effects appear to do nothing on a fresh connection.
    /// </summary>
    public void ResizeZone(int controllerIndex, int zoneIndex, int ledCount)
        => Send(controllerIndex, Pkt.RESIZE_ZONE,
                BitConverter.GetBytes(zoneIndex).Concat(BitConverter.GetBytes(ledCount)).ToArray());

    /// <summary>
    /// Expands every addressable zone that currently reports 0 LEDs to a target size,
    /// then re-enumerates so LedCount reflects the real, paintable LED total.
    /// <paramref name="sizeFor"/> may override the size per (deviceKey, zoneIndex); null = zone max.
    /// </summary>
    public void ExpandAllZones(CancellationToken ct = default, Func<RgbController, RgbZone, int>? sizeFor = null)
    {
        bool changed = false;
        foreach (var dev in Controllers)
        {
            foreach (var zone in dev.Zones)
            {
                if (!zone.IsResizable) continue;
                int want = sizeFor?.Invoke(dev, zone) ?? (int)zone.LedsMax;
                want = (int)Math.Clamp((uint)Math.Max(0, want), zone.LedsMin, zone.LedsMax);
                if (want > 0 && want != zone.LedsCount)
                {
                    ct.ThrowIfCancellationRequested();
                    ResizeZone(dev.Index, zone.Index, want);
                    changed = true;
                    Thread.Sleep(120); // the server rebuilds the controller; give it room
                }
            }
        }
        if (!changed) return;
        Thread.Sleep(600);
        RefreshControllers(ct);
    }

    /// <summary>Paint absolute LED colors on one controller. rgb length must be 3*ledCount.</summary>
    public void UpdateLeds(int controllerIndex, byte[] rgb)
    {
        int leds = rgb.Length / 3;
        if (leds <= 0) return;
        lock (_io)
        {
            int size = 4 + 2 + leds * 4;
            var payload = Rent(size);
            BitConverter.TryWriteBytes(payload.AsSpan(0), size);
            BitConverter.TryWriteBytes(payload.AsSpan(4), (ushort)leds);
            for (int i = 0; i < leds; i++)
            {
                int o = 6 + i * 4;
                payload[o] = rgb[i * 3];
                payload[o + 1] = rgb[i * 3 + 1];
                payload[o + 2] = rgb[i * 3 + 2];
                payload[o + 3] = 0;
            }
            SendLocked(controllerIndex, Pkt.UPDATE_LEDS, payload, size);
        }
    }

    /// <summary>
    /// Paint one zone. This is what actually lights ARGB fan ports and pump rings,
    /// because zone buffers are independent of the flattened device buffer on some devices.
    /// payload = u32 total_size + i32 zone_index + u16 led_count + count*(R,G,B,pad)
    /// </summary>
    public void UpdateZoneLeds(int controllerIndex, int zoneIndex, byte[] rgb)
    {
        int leds = rgb.Length / 3;
        if (leds <= 0) return;
        lock (_io)
        {
            int size = 4 + 4 + 2 + leds * 4;
            var payload = Rent(size);
            BitConverter.TryWriteBytes(payload.AsSpan(0), size);
            BitConverter.TryWriteBytes(payload.AsSpan(4), zoneIndex);
            BitConverter.TryWriteBytes(payload.AsSpan(8), (ushort)leds);
            for (int i = 0; i < leds; i++)
            {
                int o = 10 + i * 4;
                payload[o] = rgb[i * 3];
                payload[o + 1] = rgb[i * 3 + 1];
                payload[o + 2] = rgb[i * 3 + 2];
                payload[o + 3] = 0;
            }
            SendLocked(controllerIndex, Pkt.UPDATE_ZONE_LEDS, payload, size);
        }
    }

    private byte[] Rent(int size)
    {
        if (_payloadBuf.Length < size) _payloadBuf = new byte[Math.Max(size, _payloadBuf.Length * 2)];
        return _payloadBuf;
    }

    /// <summary>Switch a controller to direct/custom mode so per-LED writes take effect.</summary>
    public void SetCustomMode(int controllerIndex) => Send(controllerIndex, Pkt.SET_CUSTOM_MODE, Array.Empty<byte>());

    /// <summary>
    /// Explicitly activates a device mode by index (UPDATE_MODE). Used to force "Direct"
    /// on devices where SET_CUSTOM_MODE is a no-op, so software colors are honoured.
    /// </summary>
    public void SetMode(RgbController dev, int modeIndex)
    {
        if (modeIndex < 0 || modeIndex >= dev.Modes.Count) return;
        var m = dev.Modes[modeIndex];
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes(modeIndex));                 // i32 mode id
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(m.Name + "\0");
        body.AddRange(BitConverter.GetBytes((ushort)nameBytes.Length));  // u16 len incl NUL
        body.AddRange(nameBytes);
        body.AddRange(BitConverter.GetBytes(m.Value));
        body.AddRange(BitConverter.GetBytes(m.Flags));
        body.AddRange(BitConverter.GetBytes(m.SpeedMin));
        body.AddRange(BitConverter.GetBytes(m.SpeedMax));
        if (ProtocolVersion >= 3)
        {
            body.AddRange(BitConverter.GetBytes(m.BrightnessMin));
            body.AddRange(BitConverter.GetBytes(m.BrightnessMax));
        }
        body.AddRange(BitConverter.GetBytes(m.ColorsMin));
        body.AddRange(BitConverter.GetBytes(m.ColorsMax));
        body.AddRange(BitConverter.GetBytes(m.Speed));
        if (ProtocolVersion >= 3) body.AddRange(BitConverter.GetBytes(m.Brightness));
        body.AddRange(BitConverter.GetBytes(m.Direction));
        body.AddRange(BitConverter.GetBytes(m.ColorMode));
        body.AddRange(BitConverter.GetBytes((ushort)0)); // color count
        var payload = BitConverter.GetBytes(body.Count + 4).Concat(body).ToArray();
        Send(dev.Index, Pkt.UPDATE_MODE, payload);
    }

    /// <summary>
    /// Puts every paintable device into software-control mode: SET_CUSTOM_MODE first,
    /// then an explicit UPDATE_MODE to the "Direct" mode when the device exposes one.
    /// </summary>
    public void EnsureDirectMode()
    {
        foreach (var dev in Controllers)
        {
            if (dev.LedCount == 0) continue;
            try { SetCustomMode(dev.Index); } catch { }
            int direct = dev.DirectModeIndex;
            if (direct >= 0 && dev.ActiveMode != direct)
            {
                try { SetMode(dev, direct); } catch { }
            }
        }
        Thread.Sleep(150);
    }

    /// <summary>Persist current direct mode so lighting survives reboots (device firmware permitting).</summary>
    public void SaveMode(int controllerIndex) => Send(controllerIndex, Pkt.SAVE_MODE, Array.Empty<byte>());

    private void Send(int deviceId, uint packetType, byte[] payload)
    {
        lock (_io) SendLocked(deviceId, packetType, payload, payload.Length);
    }

    /// <summary>Caller must hold <see cref="_io"/>.</summary>
    private void SendLocked(int deviceId, uint packetType, byte[] payload, int length)
    {
        var stream = _stream ?? throw new IOException("SDK not connected");
        try
        {
            // ONE write for header+payload. Two writes with NoDelay=true put every packet on the
            // wire as two TCP segments (24 per frame at 12 zones), which measurably capped the
            // effect loop; a single buffer halves the syscalls and the segments.
            int total = 16 + length;
            if (_frameBuf.Length < total) _frameBuf = new byte[Math.Max(total, _frameBuf.Length * 2)];
            _frameBuf[0] = (byte)'O'; _frameBuf[1] = (byte)'R'; _frameBuf[2] = (byte)'G'; _frameBuf[3] = (byte)'B';
            BitConverter.TryWriteBytes(_frameBuf.AsSpan(4), (uint)deviceId);
            BitConverter.TryWriteBytes(_frameBuf.AsSpan(8), packetType);
            BitConverter.TryWriteBytes(_frameBuf.AsSpan(12), (uint)length);
            if (length > 0) Buffer.BlockCopy(payload, 0, _frameBuf, 16, length);
            stream.Write(_frameBuf, 0, total);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            Connected = false;
            // Invoke OUTSIDE the _io lock: a subscriber calling back into the client
            // (Reconnect/Disconnect) would otherwise deadlock on the same lock.
            Monitor.Exit(_io);
            try { ConnectionLost?.Invoke(e); }
            finally { Monitor.Enter(_io); }
            throw;
        }
    }

    /// <summary>Send + read one reply as an atomic exchange.</summary>
    private (uint type, byte[] payload) Request(int deviceId, uint packetType, byte[] payload, CancellationToken ct)
    {
        lock (_io)
        {
            SendLocked(deviceId, packetType, payload, payload.Length);
            return ReadPacket(ct);
        }
    }

    private (uint type, byte[] payload) ReadPacket(CancellationToken ct)
    {
        var stream = _stream ?? throw new IOException("SDK not connected");
        var header = ReadExact(stream, 16, ct);
        if (header[0] != (byte)'O' || header[1] != (byte)'R' || header[2] != (byte)'G' || header[3] != (byte)'B')
            throw new IOException("Invalid SDK packet magic");
        uint type = BitConverter.ToUInt32(header, 8);
        uint size = BitConverter.ToUInt32(header, 12);
        if (!IsPayloadSizeValid(size))
            throw new IOException($"SDK payload too large: {size}");
        var payload = size > 0 ? ReadExact(stream, (int)size, ct) : Array.Empty<byte>();
        return (type, payload);
    }

    public static bool IsPayloadSizeValid(uint size) => size <= MaxPayloadSize;

    private static byte[] ReadExact(NetworkStream s, int n, CancellationToken ct)
    {
        var buf = new byte[n];
        int off = 0;
        while (off < n)
        {
            ct.ThrowIfCancellationRequested();
            int read = s.Read(buf, off, n - off);
            if (read <= 0) throw new IOException("SDK connection closed");
            off += read;
        }
        return buf;
    }

    public void Disconnect()
    {
        lock (_io)
        {
            Connected = false;
            try { _stream?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }
            _stream = null;
            _tcp = null;
        }
    }

    public void Dispose() => Disconnect();
}
