using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace FullRGB.Setup;

/// <summary>
/// Everything the app can learn about the hardware WITHOUT owning a socket: what OpenRGB thinks
/// the devices are, which LEDs it is currently holding, and which connections exist to its port.
///
/// WHY THIS EXISTS. The user's report was "after a wake-up nothing is applied until I press
/// Rescan". The one fact the app was missing is whether the ENGINE IS STILL APPLYING FRAMES. It
/// cannot ask its own client (that is the suspect), and OpenRGB has no request that answers "are
/// you painting?", so this probes from the side:
///
///  * its own short-lived reader connection, which sees exactly what the server holds for every
///    controller — i.e. whatever client last painted (verified: a colour written by one socket is
///    read back by another, so this really does track the painter);
///  * the TCP table, for the listening socket and the number of attached clients. A session that
///    lost its engine shows up as "the port is alive but nobody is connected".
///
/// It never writes a colour: the probe must not be able to change what the user sees.
/// </summary>
public static class EngineShadow
{
    /// <summary>One device as the server currently holds it.</summary>
    public sealed class DeviceShadow
    {
        public string Name = "";
        public int LedCount;
        /// <summary>Per-zone LED counts. Zero everywhere = the zones were never expanded.</summary>
        public List<int> ZoneLeds = new();
        /// <summary>Sum of the stored LED colours: changes while an effect is applied.</summary>
        public long ColourSum;
        /// <summary>Hash of the stored LED colours, so motion is detectable even if the sum collides.</summary>
        public ulong ColourHash;
    }

    public sealed class Snapshot
    {
        public bool EngineReachable;
        /// <summary>Something is LISTENING on the SDK port (from the OS, not from a connect attempt).</summary>
        public bool PortListening = true;
        public bool TcpTableReadable = true;
        /// <summary>ESTABLISHED connections to the port, including this probe's own socket.</summary>
        public int ClientConnections;
        /// <summary>Distinct connection inodes the engine is serving — a diagnostic only.</summary>
        public int DistinctSockets;
        public string Error = "";
        public List<DeviceShadow> Devices = new();

        public int ZoneTotal => Devices.Sum(d => d.ZoneLeds.Count(z => z > 0));
        public int LedTotal => Devices.Sum(d => d.ZoneLeds.Where(z => z > 0).Sum());
        public long ColourSum => Devices.Sum(d => d.ColourSum);
        public string Signature => string.Join(";", Devices.Select(d =>
            $"{d.Name}:{d.LedCount}:{string.Join(",", d.ZoneLeds)}"));
    }

    /// <summary>What the watchdog should do about a snapshot.</summary>
    public enum Verdict
    {
        Ok,        // the engine is applying our frames (or there is nothing to apply)
        Wait,      // too early to judge: the session is still coming up, or one fluke probe
        Rebuild,   // zones/LEDs are stuck at the values the engine had before we expanded them
        Repair,    // the engine is not applying frames at all: replace it and reconnect
    }

    /// <summary>Grace after a connect/repair before any verdict is trusted.</summary>
    public const int GraceSeconds = 10;
    /// <summary>Consecutive unhealthy probes required before acting (one fluke never restarts an engine).</summary>
    public const int StrikesToAct = 2;

    /// <summary>
    /// What to do, from the raw observations. PURE — asserted by <c>--rendertest</c> §44.
    ///
    /// <paramref name="secondsSinceSession"/> is the time since the last connect/repair: inside
    /// <see cref="GraceSeconds"/> nothing is decided, because the app is legitimately still
    /// expanding zones and the device list is still growing.
    ///
    /// <paramref name="consecutiveStrikes"/> counts probes that found no evidence of painting
    /// (no reply at all, or replies whose stored colours never moved).
    /// </summary>
    public static Verdict Decide(Snapshot? now, Snapshot? before, bool effectsRunning,
                                 double secondsSinceSession, int consecutiveStrikes,
                                 int probesWithoutGrowth)
    {
        if (now is null) return Verdict.Wait;                       // no probe ran at all
        if (secondsSinceSession < GraceSeconds) return Verdict.Wait;
        if (!effectsRunning) return Verdict.Wait;                   // the user stopped the effects
        if (!now.PortListening) return Verdict.Repair;              // nothing is listening: engine died
        if (!now.TcpTableReadable) return Verdict.Wait;             // cannot see the port: don't guess
        if (!now.EngineReachable)                                   // accepts TCP, never answers
            return consecutiveStrikes >= StrikesToAct ? Verdict.Repair : Verdict.Wait;

        // The app's own client + one writer thread per device must be attached. A session that lost
        // its engine (suspend killing the sockets, an engine replaced under it) leaves the port open
        // with nothing connected to it.
        if (now.ClientConnections < 2)
            return consecutiveStrikes >= StrikesToAct ? Verdict.Repair : Verdict.Wait;

        if (now.LedTotal == 0 || now.ZoneTotal == 0)
        {
            // Nothing paintable at all. On a machine that HAS controllers this is the stalled
            // boot/wake state: the first expand ran while USB was still enumerating, so the engine
            // kept zones at 0 LEDs and every later frame had nothing to write to. Re-sampling can
            // repair that, so it is never allowed to become a process restart — and one probe on a
            // machine that has never worked (a cold boot mid-enumeration) is not a stall yet.
            if (now.Devices.Count == 0) return Verdict.Wait;
            return consecutiveStrikes >= StrikesToAct ? Verdict.Rebuild : Verdict.Wait;
        }

        if (before is not null && now.Signature == before.Signature && probesWithoutGrowth >= 3)
            return Verdict.Rebuild;    // nothing grew for a few probes: re-expand the zones

        // One strike is not a verdict: a healthy session is left alone until the evidence repeats.
        return consecutiveStrikes >= StrikesToAct ? Verdict.Repair : Verdict.Ok;
    }

    /// <summary>
    /// True when any device whose effect is animated changed its stored colours between two
    /// snapshots. A deliberately static colouring is excluded by the caller, so "no motion" here
    /// means a frame that should have changed did not.
    /// </summary>
    public static bool AnyMotion(Snapshot? before, Snapshot? now, ISet<string> animatedDevices)
    {
        if (before is null || now is null) return false;
        foreach (var d in now.Devices)
        {
            if (!animatedDevices.Contains(d.Name)) continue;
            var old = before.Devices.FirstOrDefault(x => x.Name == d.Name);
            if (old is null) return true;                 // newly appeared: assume alive
            if (old.ColourSum != d.ColourSum || old.ColourHash != d.ColourHash) return true;
        }
        return false;
    }

    // ---------- the probe itself ----------

    internal const int ReqProtocol = 40, SetClientName = 50, ReqControllerCount = 0, ReqControllerData = 1;
    private static readonly byte[] Magic = { (byte)'O', (byte)'R', (byte)'G', (byte)'B' };

    /// <summary>
    /// Reads the engine's current view of every controller. Never writes a colour; the connection
    /// is closed before returning. <paramref name="timeoutMs"/> bounds the whole probe.
    /// </summary>
    public static Snapshot Probe(int port, int timeoutMs = 1500)
    {
        var snap = new Snapshot();
        var facts = ReadTcpFacts(port);
        if (facts is null) snap.TcpTableReadable = false;
        else
        {
            snap.PortListening = facts.Value.Listening;
            snap.ClientConnections = facts.Value.Clients + 1;    // +1: this probe's own socket
            snap.DistinctSockets = facts.Value.Sockets;
        }
        if (snap.TcpTableReadable && !snap.PortListening) return snap;   // nothing to talk to

        var budget = Stopwatch.StartNew();
        try
        {
            using var tcp = new TcpClient();
            var connect = tcp.ConnectAsync("127.0.0.1", port);
            if (!connect.Wait(Math.Max(200, timeoutMs / 3)))
            {
                snap.Error = "connect timeout";
                return snap;
            }
            tcp.NoDelay = true;
            tcp.ReceiveTimeout = timeoutMs;
            tcp.SendTimeout = timeoutMs;
            using var s = tcp.GetStream();

            Write(s, 0, ReqProtocol, BitConverter.GetBytes(4u));
            int saved = tcp.ReceiveTimeout;
            tcp.ReceiveTimeout = Math.Max(150, timeoutMs / 3);
            try { Read(s, out _); } catch { /* an old server stays silent: v0 is fine for reads */ }
            tcp.ReceiveTimeout = saved;
            Write(s, 0, SetClientName, System.Text.Encoding.UTF8.GetBytes("FullRGB-shadow\0"));

            Write(s, 0, ReqControllerCount, Array.Empty<byte>());
            var countPayload = Read(s, out _);
            uint count = countPayload.Length >= 4 ? BitConverter.ToUInt32(countPayload, 0) : 0u;
            if (count > 512) count = 512;          // a bad reply must not spin
            for (uint i = 0; i < count; i++)
            {
                if (budget.ElapsedMilliseconds > timeoutMs) { snap.Error = "probe budget reached"; break; }
                Write(s, (int)i, ReqControllerData, BitConverter.GetBytes(4u));
                var data = Read(s, out _);
                var dev = ParseShadow(data);
                if (dev is not null) snap.Devices.Add(dev);
            }
            snap.EngineReachable = true;
        }
        catch (Exception e)
        {
            snap.Error = e.Message;
        }
        return snap;
    }

    /// <summary>
    /// Parses one REQUEST_CONTROLLER_DATA payload down to what the watchdog needs. Same wire layout
    /// as <see cref="SDK.DeviceParser"/> (verified byte-exact on this rig); a malformed reply is
    /// reported as null rather than throwing into the timer.
    /// </summary>
    internal static DeviceShadow? ParseShadow(byte[] d)
    {
        try
        {
            var dev = new DeviceShadow();
            int o = 8;                                       // total size prefix + device type
            dev.Name = Str(d, ref o);
            for (int i = 0; i < 5; i++) Str(d, ref o);        // vendor, description, version, serial, location

            ushort modeCount = U16(d, ref o);
            o += 4;                                          // active mode
            for (int m = 0; m < modeCount; m++)
            {
                Str(d, ref o);                               // mode name
                o += 44;                                     // value + 10 fixed u32s
                ushort cc = U16(d, ref o);
                o += cc * 4;
            }

            ushort zoneCount = U16(d, ref o);
            for (int z = 0; z < zoneCount; z++)
            {
                Str(d, ref o);                               // zone name
                o += 12;                                     // zone_type, leds_min, leds_max
                uint ledsCount = U32(d, ref o);
                dev.ZoneLeds.Add((int)ledsCount);
                ushort matrix = U16(d, ref o);
                if (matrix > 0)
                {
                    uint h = U32(d, ref o), w = U32(d, ref o);
                    o += checked((int)(h * w * 4));
                }
                ushort segs = U16(d, ref o);
                for (int sg = 0; sg < segs; sg++) { Str(d, ref o); o += 12; }
            }

            ushort leds = U16(d, ref o);
            for (int l = 0; l < leds; l++) { Str(d, ref o); o += 4; }

            ushort colors = U16(d, ref o);
            long sum = 0;
            ulong hash = 1469598103934665603UL;
            for (int c = 0; c < colors; c++)
            {
                byte r = d[o], g = d[o + 1], b = d[o + 2];
                o += 4;
                sum += r + 3L * g + 7L * b;
                hash ^= r; hash *= 1099511628211UL;
                hash ^= g; hash *= 1099511628211UL;
                hash ^= b; hash *= 1099511628211UL;
            }
            dev.LedCount = leds;
            dev.ColourSum = sum;
            dev.ColourHash = hash;
            return dev;
        }
        catch { return null; }
    }

    private static string Str(byte[] d, ref int o)
    {
        ushort len = U16(d, ref o);
        if (len == 0) return "";
        var s = System.Text.Encoding.UTF8.GetString(d, o, Math.Max(0, len - 1));
        o += len;
        return s;
    }

    private static ushort U16(byte[] d, ref int o) { ushort v = (ushort)(d[o] | (d[o + 1] << 8)); o += 2; return v; }

    private static uint U32(byte[] d, ref int o)
    {
        uint v = (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
        o += 4;
        return v;
    }

    private static void Write(NetworkStream s, int dev, int type, byte[] payload)
    {
        var buf = new byte[16 + payload.Length];
        Magic.CopyTo(buf, 0);
        BitConverter.TryWriteBytes(buf.AsSpan(4), (uint)dev);
        BitConverter.TryWriteBytes(buf.AsSpan(8), (uint)type);
        BitConverter.TryWriteBytes(buf.AsSpan(12), (uint)payload.Length);
        payload.CopyTo(buf, 16);
        s.Write(buf, 0, buf.Length);
    }

    private static byte[] Read(NetworkStream s, out uint type)
    {
        var header = ReadExact(s, 16);
        type = BitConverter.ToUInt32(header, 8);
        uint size = BitConverter.ToUInt32(header, 12);
        if (size > 1 << 20) throw new IOException("oversized SDK reply");
        return size == 0 ? Array.Empty<byte>() : ReadExact(s, (int)size);
    }

    private static byte[] ReadExact(NetworkStream s, int n)
    {
        var buf = new byte[n];
        int off = 0;
        while (off < n)
        {
            int read = s.Read(buf, off, n - off);
            if (read <= 0) throw new IOException("SDK closed mid-reply");
            off += read;
        }
        return buf;
    }

    // ---------- the TCP table ----------

    public readonly record struct TcpFacts(bool Listening, int Clients, int Sockets);

    /// <summary>
    /// Connection facts for a port, from the OS rather than from a connect attempt: is anything
    /// still LISTENING, and how many clients are attached. Returns null when the table cannot be
    /// read, and the caller then makes no claim at all (never a guessed repair).
    ///
    /// Windows source: <c>GetExtendedTcpTable</c> (iphlpapi) with OWNER_PID_ALL — the same table
    /// <c>netstat</c> prints. No process spawn, no parsing of localised text, no dependency.
    /// (Reading <c>/proc/net/tcp</c> was tried first and does NOT work: that file exists only for
    /// MSYS programs, never for a native Win32 process.)
    /// </summary>
    public static TcpFacts? ReadTcpFacts(int port) => ParseRows(ReadTcpRows(), port);

    /// <summary>One IPv4 TCP row as Windows reports it.</summary>
    public readonly record struct TcpRow(int LocalPort, int RemotePort, int State, int OwningPid);

    // MIB_TCP_STATE values, in RFC 793 order (NOT the /proc hex codes).
    internal const int StateListen = 2;
    internal const int StateEstablished = 5;

    private static List<TcpRow>? ReadTcpRows()
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int size = 0;
            // First call only sizes the table; ERROR_INSUFFICIENT_BUFFER (122) is the expected answer.
            int rc = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
            if (rc != 122 && rc != 0) return null;
            if (size <= 4) return null;
            buffer = Marshal.AllocHGlobal(size);
            rc = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
            if (rc != 0) return null;

            int count = Marshal.ReadInt32(buffer);
            var rows = new List<TcpRow>(count);
            // MIB_TCPTABLE_OWNER_PID = u32 count, then count × 6 u32:
            // state, local addr, local port, remote addr, remote port, owning pid.
            IntPtr row = buffer + 4;
            for (int i = 0; i < count; i++)
            {
                int state = Marshal.ReadInt32(row, 0);
                int local = Marshal.ReadInt32(row, 8);
                int remote = Marshal.ReadInt32(row, 16);
                int pid = Marshal.ReadInt32(row, 20);
                rows.Add(new TcpRow(NetworkPort(local), NetworkPort(remote), state, pid));
                row += 24;
            }
            return rows;
        }
        catch { return null; }
        finally { if (buffer != IntPtr.Zero) try { Marshal.FreeHGlobal(buffer); } catch { } }
    }

    /// <summary>The port sits in the LOW 16 bits of the packed address, in network byte order.</summary>
    internal static int NetworkPort(int packed)
        => ((packed & 0xFF) << 8) | ((packed >> 8) & 0xFF);

    /// <summary>
    /// PURE: the LISTENING socket for <paramref name="port"/>, and the ESTABLISHED connections
    /// attached to it. Asserted by <c>--rendertest</c> §45.
    ///
    /// Counting rule: a connection is a row whose LOCAL port is the engine's — the server side of a
    /// loopback connection is the only row carrying that local port, so nothing is double counted.
    /// TIME_WAIT rows belong to connections that are already gone and are NOT clients; that matters,
    /// because a listener left alone after a suspend is exactly the state the watchdog must catch.
    /// Returns null when there is no table to read, and then no claim is made at all.
    /// </summary>
    internal static TcpFacts? ParseRows(IReadOnlyList<TcpRow>? rows, int port)
    {
        if (rows is null) return null;
        bool listening = false;
        int clients = 0, sockets = 0;
        foreach (var r in rows)
        {
            if (r.LocalPort != port) continue;
            if (r.State == StateListen) listening = true;
            else if (r.State == StateEstablished) clients++;
            sockets++;
        }
        return new TcpFacts(listening, clients, sockets);
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder,
        int ulAf, int tableClass, int reserved);

    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;
}
