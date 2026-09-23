using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using FullRGB.Diag;

namespace FullRGB.Automation;

/// <summary>
/// The local control bus: everything an external tool needs to drive a RUNNING FullRGB.
///
/// Three transports, one command set (<see cref="CommandDispatcher"/>):
///  • CLI one-shots (<c>FullRGB.exe --set-profile gaming</c>) — resolved without any server
///    when no instance is running, else forwarded over the pipe (see ControlHub.SendOneShot).
///  • Named pipe <c>fullrgb-ctrl</c> — one request per connection, line in → line out.
///    Local scripts and AHK: <c>\\.\pipe\fullrgb-ctrl</c>. No token needed: the pipe lives in
///    the user's own session.
///  • HTTP on <c>http://127.0.0.1:9372</c> (raw TcpListener — HttpListener would need an ACL for
///    non-localhost prefixes). Every /api call needs the session token; the companion page
///    exchanges the PIN (shown in Settings) for it once. The same listener also serves the
///    mobile companion page, and binds to ALL interfaces only when the companion is enabled.
/// </summary>
public sealed class ControlHub : IDisposable
{
    public const string PipeName = "fullrgb-ctrl";
    private static readonly char[] PinChars = "0123456789".ToCharArray();

    private readonly Dispatcher _dispatcher;
    private readonly IControlTarget _target;
    private readonly bool _companion;
    private CancellationTokenSource? _cts;
    private Task? _pipeLoop;
    private Task? _httpLoop;
    private TcpListener? _listener;

    /// <summary>Token for the HTTP API. Random per start; also written to %APPDATA%\FullRGB\api-token.</summary>
    public string ApiToken { get; } = NewToken();
    public string CompanionPin { get; } = NewPin();

    /// <summary>PIN auth failure timestamps (brute-force guard), last 60 s window.</summary>
    private readonly Queue<long> _authFailures = new();
    private readonly object _authLock = new();

    /// <summary>Global request timestamps (throttle for EVERY route, not just /api/auth),
    /// last 1 s window.</summary>
    private readonly Queue<long> _requestTimes = new();
    private readonly object _rateLock = new();

    /// <summary>Caps concurrent HTTP connections. Without it, a peer on the same LAN can hold
    /// hundreds of sockets open and starve the pool (slowloris-style).</summary>
    private readonly SemaphoreSlim _httpSlots = new(MaxConcurrentHttp, MaxConcurrentHttp);

    private const int MaxConcurrentHttp = 16;
    private const int MaxRequestsPerSecond = 40;
    /// <summary>How long an accepted socket waits for a free worker before it is dropped.</summary>
    private static readonly TimeSpan SlotWait = TimeSpan.FromSeconds(2);

    private ControlHub(Dispatcher dispatcher, IControlTarget target, bool companion)
    {
        _dispatcher = dispatcher;
        _target = target;
        _companion = companion;
    }

    public static ControlHub Start(Dispatcher dispatcher, IControlTarget target, int port, bool companion)
    {
        var hub = new ControlHub(dispatcher, target, companion);
        hub._cts = new CancellationTokenSource();
        var ct = hub._cts.Token;
        hub._pipeLoop = Task.Run(() => hub.PipeLoop(ct), ct);
        hub._httpLoop = Task.Run(() => hub.HttpLoop(port, ct), ct);
        try
        {
            // Local scripts may read the token instead of scraping the UI. Same-user only.
            var tokenPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FullRGB", "api-token");
            Directory.CreateDirectory(Path.GetDirectoryName(tokenPath)!);
            File.WriteAllText(tokenPath, hub.ApiToken + Environment.NewLine);
        }
        catch { }
        AppLog.Info($"control bus started (pipe={PipeName}, http=127.0.0.1:{port}, companion={companion})");
        return hub;
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _httpSlots.Dispose(); } catch { }
        try
        {
            var tokenPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FullRGB", "api-token");
            if (File.Exists(tokenPath)) File.Delete(tokenPath);
        }
        catch { }
    }

    private static string NewToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();

    private static string NewPin()
    {
        var rng = RandomNumberGenerator.Create();
        return new string(Enumerable.Range(0, 6)
            .Select(_ => PinChars[RandomNumberGenerator.GetInt32(0, PinChars.Length)]).ToArray());
    }

    // ---------------------------------------------------------------- named pipe

    private async Task PipeLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var _ = server;

                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                                                    bufferSize: 1024, leaveOpen: true);
                string? line = await ReadLineWithTimeoutAsync(reader, 2500).ConfigureAwait(false);
                string reply = DispatchOnUiThread(line);
                await using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true)
                { AutoFlush = true };
                await writer.WriteAsync(reply + "\n").ConfigureAwait(false);
                // One request per connection: the client sees the reply and goes away.
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                AppLog.Warn("pipe loop: " + e.Message);
                try { await Task.Delay(800, ct).ConfigureAwait(false); } catch { break; }
            }
            finally
            {
                try { server?.Dispose(); } catch { }
            }
        }
    }

    private static async Task<string?> ReadLineWithTimeoutAsync(StreamReader reader, int timeoutMs)
    {
        var readTask = reader.ReadLineAsync();
        var done = await Task.WhenAny(readTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
        return done == readTask ? await readTask.ConfigureAwait(false) : null;
    }

    /// <summary>Runs one dispatcher command on the UI thread and returns the reply text.</summary>
    private string DispatchOnUiThread(string? line)
    {
        try
        {
            return _dispatcher.Invoke(() => CommandDispatcher.Execute(_target, line),
                DispatcherPriority.Send);
        }
        catch (Exception e)
        {
            AppLog.Warn("dispatch: " + e.Message);
            return "ERR " + e.Message;
        }
    }

    /// <summary>
    /// One-shot pipe client used by the CLI verbs when an instance is already running.
    /// Returns (reachable, replyText).
    /// </summary>
    public static async Task<(bool ok, string reply)> SendOneShot(string command, int timeoutMs = 2500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var cts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync(timeoutMs, cts.Token).ConfigureAwait(false);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), 256, leaveOpen: true) { AutoFlush = true };
            await writer.WriteAsync(command + "\n").ConfigureAwait(false);
            using var reader = new StreamReader(client, Encoding.UTF8, false, 256, leaveOpen: true);
            var read = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
            return (read is not null && !read.StartsWith("ERR", StringComparison.Ordinal), read ?? "no reply");
        }
        catch (Exception)
        {
            return (false, "");
        }
    }

    // ---------------------------------------------------------------- HTTP

    private async Task HttpLoop(int port, CancellationToken ct)
    {
        // Loopback only, unless the companion is on (then all interfaces so a phone can reach it).
        _listener = new TcpListener(_companion ? IPAddress.Any : IPAddress.Loopback, port);
        try
        {
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start();
        }
        catch (Exception e)
        {
            AppLog.Warn($"control API port {port} unavailable: {e.Message}");
            try { _listener.Stop(); } catch { }
            _listener = null;
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                // Back-pressure instead of an unbounded Task.Run per connection: when every
                // worker is busy the socket is dropped rather than queued forever, so a peer
                // that opens sockets and never finishes a request cannot exhaust the pool.
                if (!await _httpSlots.WaitAsync(SlotWait, ct).ConfigureAwait(false))
                {
                    try { client.Dispose(); } catch { }
                    client = null;
                    continue;
                }
                // No token on Task.Run: the delegate must always run so it can release the slot.
                _ = Task.Run(() => HandleHttpAsync(client, ct));
                client = null;   // HandleHttpAsync owns the socket from here
            }
            catch (OperationCanceledException) { break; }
            catch (Exception)
            {
                try { client?.Dispose(); } catch { }
                try { await Task.Delay(500, ct).ConfigureAwait(false); } catch { break; }
            }
        }
        try { _listener.Stop(); } catch { }
    }

    private async Task HandleHttpAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using var _c = client;
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;
            var stream = client.GetStream();
            var req = await HttpRequest.ReadAsync(stream, ct).ConfigureAwait(false);
            if (req is null) return;

            var (status, contentType, body) = Route(req);
            byte[] payload = Encoding.UTF8.GetBytes(body);
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append("\r\n");
            sb.Append("Content-Type: ").Append(contentType).Append("; charset=utf-8\r\n");
            sb.Append("Content-Length: ").Append(payload.Length).Append("\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            sb.Append("Connection: close\r\n\r\n");
            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(head.AsMemory(0, head.Length), ct).ConfigureAwait(false);
            await stream.WriteAsync(payload.AsMemory(0, payload.Length), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch { /* a dropped phone or a port scanner must not log spam */ }
        finally { try { _httpSlots.Release(); } catch { } }
    }

    /// <summary>Constant-time string compare — the token and the PIN must not leak their
    /// contents through the time their comparison takes.</summary>
    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a ?? ""), Encoding.UTF8.GetBytes(b ?? ""));

    /// <summary>Throttle shared by every route. The dispatcher runs on the UI thread, so an
    /// unthrottled flood of /api/color calls would freeze the window, not just the server.</summary>
    private bool RateLimitOk()
    {
        lock (_rateLock)
        {
            long now = Environment.TickCount64;
            while (_requestTimes.Count > 0 && now - _requestTimes.Peek() > 1000) _requestTimes.Dequeue();
            if (_requestTimes.Count >= MaxRequestsPerSecond) return false;
            _requestTimes.Enqueue(now);
            return true;
        }
    }

    /// <summary>Route table. Returns (status line, content type, body).</summary>
    private (string, string, string) Route(HttpRequest req)
    {
        if (!RateLimitOk())
            return ("429 Too Many Requests", "application/json",
                CommandDispatcher.JsonErr("too many requests, slow down"));

        string path = req.Path;
        string token = req.Query("token");
        if (token.Length == 0) token = req.Header("x-fullrgb-token");

        // Unauthenticated: the page itself + the PIN exchange + a bare health ping.
        if (req.Method == "GET" && (path == "/" || path == "/index.html"))
            return ("200 OK", "text/html", CompanionPage.Html);

        if (req.Method == "POST" && path == "/api/auth")
        {
            if (!BruteForceOk()) return ("429 Too Many Requests", "application/json",
                CommandDispatcher.JsonErr("too many attempts, wait a minute"));
            string pin = CommandDispatcher.JsonGet(req.Body, "pin");
            if (FixedTimeEquals(pin, _target.CompanionPin)) return ("200 OK", "application/json",
                CommandDispatcher.JsonOk($"\"token\":\"{ApiToken}\""));
            NoteAuthFailure();
            return ("403 Forbidden", "application/json", CommandDispatcher.JsonErr("wrong pin"));
        }

        if (req.Method == "GET" && path == "/api/health")
            return ("200 OK", "application/json", "{\"ok\":true,\"app\":\"FullRGB\"}");

        // Everything below needs the session token (even from localhost — a malicious web
        // page in the user's browser could otherwise drive DNS-rebinding/CSRF calls here).
        if (!FixedTimeEquals(token, ApiToken))
            return ("401 Unauthorized", "application/json", CommandDispatcher.JsonErr("missing or wrong token"));

        string CallText(string command) => DispatchOnUiThread(command);

        if (req.Method == "GET" && (path == "/api/status" || path == "/api/state"))
            return ("200 OK", "application/json", JsonOrError(CallText("status"), "status"));

        if (req.Method == "GET" && path == "/api/profiles")
        {
            string joined = CallText("profiles");
            var arr = string.Join(",", joined.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                             .Select(n => System.Text.Json.JsonSerializer.Serialize(n.Trim())));
            return ("200 OK", "application/json", CommandDispatcher.JsonOk($"\"profiles\":[{arr}]"));
        }

        if (req.Method == "POST" && path == "/api/profile")
        {
            string name = CommandDispatcher.JsonGet(req.Body, "name", "profile");
            string reply = CallText("set-profile " + name);
            return reply == "OK"
                ? ("200 OK", "application/json", CommandDispatcher.JsonOk())
                : ("400 Bad Request", "application/json", CommandDispatcher.JsonErr(reply));
        }

        if (req.Method == "POST" && path == "/api/effect")
        {
            string type = CommandDispatcher.JsonGet(req.Body, "type", "effect");
            string reply = CallText("set-effect " + type);
            return reply == "OK"
                ? ("200 OK", "application/json", CommandDispatcher.JsonOk())
                : ("400 Bad Request", "application/json", CommandDispatcher.JsonErr(reply));
        }

        if (req.Method == "POST" && path == "/api/color")
        {
            string hex = CommandDispatcher.JsonGet(req.Body, "hex", "color");
            bool secondary = CommandDispatcher.JsonGet(req.Body, "secondary") is "true" or "1";
            string reply = CallText((secondary ? "set-color2 " : "set-color ") + hex);
            return reply == "OK"
                ? ("200 OK", "application/json", CommandDispatcher.JsonOk())
                : ("400 Bad Request", "application/json", CommandDispatcher.JsonErr(reply));
        }

        if (req.Method == "POST" && path == "/api/brightness")
        {
            string v = CommandDispatcher.JsonGet(req.Body, "value", "brightness");
            string reply = CallText("set-brightness " + v);
            return reply.StartsWith("OK", StringComparison.Ordinal)
                ? ("200 OK", "application/json", CommandDispatcher.JsonOk())
                : ("400 Bad Request", "application/json", CommandDispatcher.JsonErr(reply));
        }

        if (req.Method == "POST" && path == "/api/speed")
        {
            string v = CommandDispatcher.JsonGet(req.Body, "value", "speed");
            string reply = CallText("set-speed " + v);
            return reply.StartsWith("OK", StringComparison.Ordinal)
                ? ("200 OK", "application/json", CommandDispatcher.JsonOk())
                : ("400 Bad Request", "application/json", CommandDispatcher.JsonErr(reply));
        }

        if (req.Method == "POST" && path == "/api/power")
        {
            string on = CommandDispatcher.JsonGet(req.Body, "on", "value");
            string reply = CallText("power " + (on is "true" or "1" ? "on" : "off"));
            return reply == "OK"
                ? ("200 OK", "application/json", CommandDispatcher.JsonOk())
                : ("400 Bad Request", "application/json", CommandDispatcher.JsonErr(reply));
        }

        if (req.Method == "POST" && path == "/api/blackout")
        {
            string reply = CallText("blackout");
            return reply == "OK"
                ? ("200 OK", "application/json", CommandDispatcher.JsonOk())
                : ("400 Bad Request", "application/json", CommandDispatcher.JsonErr(reply));
        }

        if (req.Method == "POST" && (path == "/api/event" || path == "/api/game-event"))
        {
            string name = CommandDispatcher.JsonGet(req.Body, "event", "name", "type");
            string val = CommandDispatcher.JsonGet(req.Body, "value", "v");
            string reply = CallText("game-event " + name + (val.Length > 0 ? " " + val : ""));
            return reply == "OK"
                ? ("200 OK", "application/json", CommandDispatcher.JsonOk())
                : ("400 Bad Request", "application/json", CommandDispatcher.JsonErr(reply));
        }

        if (req.Method == "POST" && path == "/api/rescan")
        {
            string reply = CallText("rescan");
            return reply == "OK"
                ? ("200 OK", "application/json", CommandDispatcher.JsonOk())
                : ("400 Bad Request", "application/json", CommandDispatcher.JsonErr(reply));
        }

        return ("404 Not Found", "application/json", CommandDispatcher.JsonErr("no such route: " + path));
    }

    /// <summary>The dispatcher returns hand-built JSON. Validate it before it goes on the wire,
    /// so a future change to that internal text format surfaces as a clean error object instead
    /// of a body that silently breaks every client's JSON parser.</summary>
    private static string JsonOrError(string raw, string what)
    {
        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(raw);
            return raw;
        }
        catch
        {
            AppLog.Warn($"dispatcher reply for '{what}' was not valid JSON");
            return CommandDispatcher.JsonErr($"{what} reply was not valid JSON");
        }
    }

    private void NoteAuthFailure()
    {
        lock (_authLock)
        {
            long now = Environment.TickCount64;
            _authFailures.Enqueue(now);
            while (_authFailures.Count > 5) _authFailures.Dequeue();
        }
    }

    private bool BruteForceOk()
    {
        lock (_authLock)
        {
            long now = Environment.TickCount64;
            while (_authFailures.Count > 0 && now - _authFailures.Peek() > 60_000) _authFailures.Dequeue();
            return _authFailures.Count < 5;
        }
    }

    /// <summary>Minimal HTTP/1.1 request: request line, headers, bounded body.</summary>
    public sealed class HttpRequest
    {
        public string Method { get; init; } = "";
        public string Path { get; init; } = "";
        public string RawQuery { get; init; } = "";
        private readonly Dictionary<string, string> _headers;
        private readonly Dictionary<string, string> _query;
        public string Body { get; init; } = "";

        public HttpRequest(string method, string path, string rawQuery,
                           Dictionary<string, string> headers, Dictionary<string, string> query, string body)
        {
            Method = method; Path = path; RawQuery = rawQuery; _headers = headers; _query = query; Body = body;
        }

        public string Header(string name)
            => _headers.TryGetValue(name, out var v) ? v : _headers.TryGetValue(name.ToLowerInvariant(), out var v2) ? v2 : "";

        public string Query(string name)
            => _query.TryGetValue(name, out var v) ? v : "";

        public static async Task<HttpRequest?> ReadAsync(NetworkStream stream, CancellationToken ct)
        {
            var buf = new MemoryStream();
            byte[] chunk = new byte[4096];
            int headerEnd = -1;
            // read until \r\n\r\n (header end) or cap
            while (headerEnd < 0 && buf.Length < 32 * 1024)
            {
                int n = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false);
                if (n <= 0) break;
                buf.Write(chunk, 0, n);
                headerEnd = FindHeaderEnd(buf);
            }
            if (buf.Length == 0) return null;
            byte[] all = buf.ToArray();
            if (headerEnd < 0) return null;

            string headText = Encoding.UTF8.GetString(all, 0, headerEnd);
            var lines = headText.Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length < 2) return null;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in lines.Skip(1))
            {
                int colon = l.IndexOf(':');
                if (colon > 0) headers[l[..colon].Trim()] = l[(colon + 1)..].Trim();
            }

            int contentLength = 0;
            if (headers.TryGetValue("Content-Length", out var cl) && !int.TryParse(cl, out contentLength)) contentLength = 0;
            contentLength = Math.Clamp(contentLength, 0, 64 * 1024);

            string body = "";
            int have = all.Length - (headerEnd + 4);
            if (have < contentLength)
            {
                byte[] rest = new byte[contentLength - have];
                int got = 0;
                while (got < rest.Length)
                {
                    int n = await stream.ReadAsync(rest.AsMemory(got, rest.Length - got), ct).ConfigureAwait(false);
                    if (n <= 0) break;
                    got += n;
                }
                body = Encoding.UTF8.GetString(rest, 0, got);
            }
            else if (have > 0)
            {
                body = Encoding.UTF8.GetString(all, headerEnd + 4, Math.Min(have, contentLength));
            }

            string target = first[1];
            string path = target, query = "";
            int q = target.IndexOf('?');
            if (q >= 0) { path = target[..q]; query = target[(q + 1)..]; }
            var queryParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = kv.IndexOf('=');
                string k = eq < 0 ? kv : kv[..eq];
                string v = eq < 0 ? "" : Uri.UnescapeDataString(kv[(eq + 1)..]);
                queryParams[Uri.UnescapeDataString(k)] = v;
            }
            path = Uri.UnescapeDataString(path);
            return new HttpRequest(first[0].ToUpperInvariant(), path, query, headers, queryParams, body);
        }

        private static int FindHeaderEnd(MemoryStream ms)
        {
            byte[] a = ms.GetBuffer();
            int len = (int)ms.Length;
            for (int i = 0; i + 3 < len; i++)
                if (a[i] == 13 && a[i + 1] == 10 && a[i + 2] == 13 && a[i + 3] == 10) return i;
            return -1;
        }
    }
}
