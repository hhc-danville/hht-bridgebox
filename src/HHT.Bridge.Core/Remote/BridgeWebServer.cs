using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using HHT.Bridge.Core.Ch9329;
using HHT.Bridge.Core.Hid;

namespace HHT.Bridge.Core.Remote;

/// <summary>What the customer is asked to sign for (shown on the tablet).</summary>
public sealed record SignOffRequest(string Shop, string? Ticket, string Summary, string? Total);

public sealed record SignOff(string Name, DateTime Time, byte[] SignaturePng, string AgreedSummary, string FromAddress);

public sealed record BridgeStatus(string BenchId, string BenchName, string? Ticket, string? Technician, bool Video, int Width, int Height,
    double Fps, bool ControlConnected, bool TargetUsb, bool ControlAllowed, int Viewers, bool RemoteController);

/// <summary>What the web server needs from the console.</summary>
public interface IBridgeHost
{
    /// <summary>Latest frame as JPEG plus a sequence number that changes with every new frame (null when no video).</summary>
    (byte[]? Jpeg, long Sequence) GetFrame();
    BridgeStatus GetStatus();
    void RemoteKeyboard(KeyboardReport report);
    void RemoteMouse(Ch9329.MouseButtons buttons, int x, int y, sbyte wheel);
    void RemoteSpecialKey(string id);
    void RemoteText(string text);
    void RemoteReleaseAll();
    void RemoteEvent(string message);
}

public sealed class BridgeWebOptions
{
    public int Port { get; init; } = 8443;
    /// <summary>Shared shop access key. Required for every API call.</summary>
    public required string AccessKey { get; init; }
    /// <summary>HTTPS certificate. Null = plain HTTP (tests only; the console always uses HTTPS).</summary>
    public X509Certificate2? Certificate { get; init; }
    /// <summary>Evaluated per request: is remote keyboard/mouse control allowed right now (technician consent)?</summary>
    public Func<bool> AllowControl { get; init; } = () => false;
    public int StreamFps { get; init; } = 15;
    public IPAddress BindAddress { get; init; } = IPAddress.Any;
}

/// <summary>
/// Small self-contained HTTPS server for remote viewing (items 7 and 8):
///   GET  /                         web viewer (asks for the access key)
///   GET  /dashboard                multi-bench dashboard
///   GET  /api/status               JSON status                       (key)
///   GET  /api/snapshot.jpg         current frame                     (key)
///   GET  /api/stream               MJPEG stream                      (key)
///   GET  /api/benches              benches on the LAN                 (key)
///   GET  /api/bench/{id}/snapshot.jpg  proxied snapshot of another bench (key)
///   GET  /api/ws                   WebSocket for keyboard/mouse      (key + technician allowed control)
/// The key may be sent as ?k=, header X-Bridge-Key. Ten bad keys from one address within five
/// minutes locks that address out for five minutes. Only one remote controller at a time.
/// </summary>
public sealed class BridgeWebServer : IDisposable
{
    private readonly IBridgeHost _host;
    private readonly BridgeWebOptions _opt;
    private readonly BenchDirectory? _benches;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, (int Count, DateTime First)> _failures = new();
    private readonly ConcurrentDictionary<string, HttpClient> _pinned = new();
    private TcpListener? _listener;
    private int _viewers;
    private string? _controllerId;
    private readonly object _controlGate = new();

    public BridgeWebServer(IBridgeHost host, BridgeWebOptions options, BenchDirectory? benches = null, Action<string>? log = null)
    {
        _host = host;
        _opt = options;
        _benches = benches;
        _log = log;
    }

    public int Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? _opt.Port;
    public int Viewers => Volatile.Read(ref _viewers);
    public bool HasRemoteController { get { lock (_controlGate) return _controllerId != null; } }
    public event Action? ViewersChanged;

    public void Start()
    {
        _listener = new TcpListener(_opt.BindAddress, _opt.Port);
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    /// <summary>Drops the current remote controller (keys released). Viewers stay connected.</summary>
    public void RevokeControl()
    {
        lock (_controlGate) _controllerId = null;
        _host.RemoteReleaseAll();
        ViewersChanged?.Invoke();
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private sealed record Request(string Method, string Path, Dictionary<string, string> Query, Dictionary<string, string> Headers, string Remote, byte[] Body);

    public const int MaxBodyBytes = 512 * 1024;

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        client.NoDelay = true;
        var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
        Stream stream = client.GetStream();
        try
        {
            if (_opt.Certificate != null)
            {
                var ssl = new SslStream(stream, false);
                using var hs = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                hs.CancelAfter(TimeSpan.FromSeconds(10));
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _opt.Certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateRequired = false,
                }, hs.Token);
                stream = ssl;
            }
            var req = await ReadRequestAsync(stream, remote);
            if (req == null) return;
            await RouteAsync(stream, req);
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException or SocketException or ObjectDisposedException or WebSocketException)
        {
            // client went away / bad TLS handshake
        }
        finally
        {
            stream.Dispose();
        }
    }

    private static async Task<Request?> ReadRequestAsync(Stream s, string remote)
    {
        var buf = new byte[8192];
        int len = 0, headerEnd = -1;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            if (len == buf.Length) return null; // headers too large
            int n = await s.ReadAsync(buf.AsMemory(len), timeout.Token);
            if (n == 0) return null;
            len += n;
            var end = buf.AsSpan(0, len).IndexOf("\r\n\r\n"u8);
            if (end >= 0) { headerEnd = end; break; }
        }
        var lines = Encoding.ASCII.GetString(buf, 0, headerEnd).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 3) return null;
        var target = first[1];
        var qIdx = target.IndexOf('?');
        var path = qIdx >= 0 ? target[..qIdx] : target;
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (qIdx >= 0)
            foreach (var pair in target[(qIdx + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var k = Uri.UnescapeDataString(eq >= 0 ? pair[..eq] : pair);
                var v = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')) : "";
                query[k] = v;
            }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var c = line.IndexOf(':');
            if (c > 0) headers[line[..c].Trim()] = line[(c + 1)..].Trim();
        }
        // Body (only small JSON bodies are accepted, e.g. the customer signature).
        byte[] body = Array.Empty<byte>();
        if (headers.TryGetValue("Content-Length", out var clText) && int.TryParse(clText, out var cl) && cl > 0)
        {
            if (cl > MaxBodyBytes) return new Request("TOO_LARGE", "/", query, headers, remote, Array.Empty<byte>());
            body = new byte[cl];
            int already = Math.Min(len - (headerEnd + 4), cl);
            Array.Copy(buf, headerEnd + 4, body, 0, already);
            int got = already;
            while (got < cl)
            {
                int n = await s.ReadAsync(body.AsMemory(got, cl - got), timeout.Token);
                if (n == 0) return null;
                got += n;
            }
        }
        return new Request(first[0].ToUpperInvariant(), Uri.UnescapeDataString(path), query, headers, remote, body);
    }

    private bool LockedOut(string ip) =>
        _failures.TryGetValue(ip, out var f) && f.Count >= 10 && DateTime.UtcNow - f.First < TimeSpan.FromMinutes(5);

    private bool Authorized(Request r)
    {
        string key = r.Query.TryGetValue("k", out var q) ? q : r.Headers.TryGetValue("X-Bridge-Key", out var h) ? h : "";
        bool ok = _opt.AccessKey.Length >= 8 && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(key.Trim().ToUpperInvariant()), Encoding.UTF8.GetBytes(_opt.AccessKey.Trim().ToUpperInvariant()));
        if (ok) { _failures.TryRemove(r.Remote, out _); return true; }
        _failures.AddOrUpdate(r.Remote, (1, DateTime.UtcNow),
            (_, f) => DateTime.UtcNow - f.First > TimeSpan.FromMinutes(5) ? (1, DateTime.UtcNow) : (f.Count + 1, f.First));
        if (_failures[r.Remote].Count == 10) _log?.Invoke($"Remote viewer: {r.Remote} locked out after 10 wrong access keys.");
        return false;
    }

    private async Task RouteAsync(Stream s, Request r)
    {
        if (r.Method == "TOO_LARGE") { await RespondAsync(s, 413, "text/plain", "Too large"u8.ToArray()); return; }
        if (r.Path == "/sign") { await RespondAsync(s, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(WebPages.SignOff), html: true); return; }
        if (r.Path.StartsWith("/api/sign", StringComparison.Ordinal)) { await SignOffAsync(s, r); return; }
        if (r.Method != "GET") { await RespondAsync(s, 405, "text/plain", "Method not allowed"u8.ToArray()); return; }

        if (r.Path is "/" or "/index.html") { await RespondAsync(s, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(WebPages.Viewer), html: true); return; }
        if (r.Path == "/dashboard") { await RespondAsync(s, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(WebPages.Dashboard), html: true); return; }
        if (r.Path == "/favicon.ico") { await RespondAsync(s, 204, "text/plain", Array.Empty<byte>()); return; }
        if (!r.Path.StartsWith("/api/", StringComparison.Ordinal)) { await RespondAsync(s, 404, "text/plain", "Not found"u8.ToArray()); return; }

        if (LockedOut(r.Remote)) { await RespondAsync(s, 429, "text/plain", "Too many wrong keys. Try again later."u8.ToArray()); return; }
        if (!Authorized(r)) { await RespondAsync(s, 401, "text/plain", "Wrong or missing access key"u8.ToArray()); return; }

        switch (r.Path)
        {
            case "/api/status":
                await RespondJsonAsync(s, _host.GetStatus() with { Viewers = Viewers, RemoteController = HasRemoteController });
                return;
            case "/api/snapshot.jpg":
            {
                var (jpeg, _) = _host.GetFrame();
                if (jpeg == null) await RespondAsync(s, 503, "text/plain", "No video"u8.ToArray());
                else await RespondAsync(s, 200, "image/jpeg", jpeg);
                return;
            }
            case "/api/stream":
                await StreamAsync(s, r);
                return;
            case "/api/benches":
            {
                var now = DateTime.UtcNow;
                var list = _benches?.List(now) ?? Array.Empty<BenchInfo>();
                await RespondJsonAsync(s, list.Select(b => new { b.Id, b.Name, b.Address, b.Port, b.Ticket, b.Video, b.TargetUsb, b.Viewers, b.IsSelf }));
                return;
            }
            case "/api/ws":
                await WebSocketAsync(s, r);
                return;
        }

        // /api/bench/{id}/snapshot.jpg
        var parts = r.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 4 && parts[1] == "bench" && parts[3] == "snapshot.jpg" && _benches != null)
        {
            var bench = _benches.Find(parts[2], DateTime.UtcNow);
            if (bench == null) { await RespondAsync(s, 404, "text/plain", "Unknown bench"u8.ToArray()); return; }
            byte[]? img = bench.IsSelf ? _host.GetFrame().Jpeg : await FetchRemoteSnapshotAsync(bench);
            if (img == null) await RespondAsync(s, 503, "text/plain", "No video"u8.ToArray());
            else await RespondAsync(s, 200, "image/jpeg", img);
            return;
        }
        await RespondAsync(s, 404, "text/plain", "Not found"u8.ToArray());
    }

    // ------------------------------------------------------------------ customer sign-off (item 9)

    private SignOffRequest? _signOff;
    private string? _signCode;
    private DateTime _signExpires;
    private readonly object _signGate = new();

    /// <summary>Raised (on a server thread) when the customer signs.</summary>
    public event Action<SignOff>? SignedOff;

    /// <summary>Opens a one-time sign-off for 30 minutes and returns the 6-digit code to give the customer.</summary>
    public string StartSignOff(SignOffRequest request)
    {
        lock (_signGate)
        {
            _signOff = request;
            _signCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            _signExpires = DateTime.UtcNow.AddMinutes(30);
            return _signCode;
        }
    }

    public void CancelSignOff() { lock (_signGate) { _signOff = null; _signCode = null; } }

    private async Task SignOffAsync(Stream s, Request r)
    {
        if (LockedOut(r.Remote)) { await RespondAsync(s, 429, "text/plain", "Too many wrong codes. Ask the technician."u8.ToArray()); return; }
        SignOffRequest? req;
        lock (_signGate)
        {
            var code = r.Query.TryGetValue("code", out var c) ? c.Trim() : "";
            bool ok = _signCode != null && DateTime.UtcNow < _signExpires &&
                      CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(code), Encoding.ASCII.GetBytes(_signCode));
            req = ok ? _signOff : null;
        }
        if (req == null)
        {
            _failures.AddOrUpdate(r.Remote, (1, DateTime.UtcNow), (_, f) => DateTime.UtcNow - f.First > TimeSpan.FromMinutes(5) ? (1, DateTime.UtcNow) : (f.Count + 1, f.First));
            await RespondAsync(s, 401, "text/plain", "Wrong or expired code"u8.ToArray());
            return;
        }
        if (r.Method == "GET" && r.Path == "/api/sign/info") { await RespondJsonAsync(s, req); return; }
        if (r.Method != "POST" || r.Path != "/api/sign") { await RespondAsync(s, 405, "text/plain", "Method not allowed"u8.ToArray()); return; }

        string name; byte[] png;
        try
        {
            using var doc = JsonDocument.Parse(r.Body);
            var root = doc.RootElement;
            name = (root.GetProperty("name").GetString() ?? "").Trim();
            bool agree = root.TryGetProperty("agree", out var ag) && ag.ValueKind == JsonValueKind.True;
            var data = root.GetProperty("png").GetString() ?? "";
            if (data.StartsWith("data:image/png;base64,", StringComparison.Ordinal)) data = data["data:image/png;base64,".Length..];
            png = Convert.FromBase64String(data);
            if (!agree || name.Length is < 2 or > 80) throw new FormatException("Name and agreement are required.");
            if (png.Length is < 100 or > 400_000 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
                throw new FormatException("Signature image invalid.");
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            await RespondAsync(s, 400, "text/plain", Encoding.UTF8.GetBytes(ex.Message));
            return;
        }
        lock (_signGate) { _signCode = null; _signOff = null; } // single use
        var signOff = new SignOff(name, DateTime.Now, png, req.Summary, r.Remote);
        SignedOff?.Invoke(signOff);
        _host.RemoteEvent($"Customer sign-off received from {name}.");
        await RespondJsonAsync(s, new { ok = true });
    }

    private async Task<byte[]?> FetchRemoteSnapshotAsync(BenchInfo b)
    {
        if (string.IsNullOrEmpty(b.Fingerprint)) return null;
        var http = _pinned.GetOrAdd(b.Fingerprint, fp => TlsCertificates.PinnedClient(fp));
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{b.Address}:{b.Port}/api/snapshot.jpg");
            req.Headers.Add("X-Bridge-Key", _opt.AccessKey);
            using var resp = await http.SendAsync(req, _cts.Token);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsByteArrayAsync(_cts.Token) : null;
        }
        catch (Exception) { return null; }
    }

    private async Task StreamAsync(Stream s, Request r)
    {
        var header = "HTTP/1.1 200 OK\r\nContent-Type: multipart/x-mixed-replace; boundary=hhtframe\r\n" +
                     "Cache-Control: no-store\r\nPragma: no-cache\r\nConnection: close\r\nX-Content-Type-Options: nosniff\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(header), _cts.Token);
        Interlocked.Increment(ref _viewers);
        ViewersChanged?.Invoke();
        _host.RemoteEvent($"Remote viewer connected from {r.Remote}.");
        long lastSeq = -1;
        var lastSent = DateTime.MinValue;
        int delay = 1000 / Math.Clamp(_opt.StreamFps, 1, 30);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var (jpeg, seq) = _host.GetFrame();
                // Re-send the same frame every 2 s so a static screen still detects departed viewers.
                if (jpeg != null && (seq != lastSeq || DateTime.UtcNow - lastSent > TimeSpan.FromSeconds(2)))
                {
                    lastSeq = seq;
                    lastSent = DateTime.UtcNow;
                    var part = Encoding.ASCII.GetBytes($"--hhtframe\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n");
                    await s.WriteAsync(part, _cts.Token);
                    await s.WriteAsync(jpeg, _cts.Token);
                    await s.WriteAsync("\r\n"u8.ToArray(), _cts.Token);
                    await s.FlushAsync(_cts.Token);
                }
                await Task.Delay(delay, _cts.Token);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _viewers);
            ViewersChanged?.Invoke();
            _host.RemoteEvent($"Remote viewer from {r.Remote} disconnected.");
        }
    }

    private async Task WebSocketAsync(Stream s, Request r)
    {
        if (!r.Headers.TryGetValue("Upgrade", out var up) || !up.Equals("websocket", StringComparison.OrdinalIgnoreCase)
            || !r.Headers.TryGetValue("Sec-WebSocket-Key", out var wsKey))
        {
            await RespondAsync(s, 400, "text/plain", "WebSocket upgrade required"u8.ToArray());
            return;
        }
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(wsKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await s.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n"), _cts.Token);

        using var ws = WebSocket.CreateFromStream(s, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(20) });
        string id = Guid.NewGuid().ToString("N");
        var keyboard = new KeyboardState();
        bool owner = false;

        async Task Send(object o)
        {
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(o, WebJson), WebSocketMessageType.Text, true, _cts.Token);
        }

        bool TryTakeControl()
        {
            if (!_opt.AllowControl()) return false;
            lock (_controlGate)
            {
                if (_controllerId == null || _controllerId == id) { _controllerId = id; return true; }
                return false;
            }
        }

        try
        {
            owner = TryTakeControl();
            if (owner) { _host.RemoteEvent($"Remote control granted to {r.Remote}."); ViewersChanged?.Invoke(); }
            await Send(new { t = "hello", control = owner, reason = owner ? "" : _opt.AllowControl() ? "busy" : "not-allowed" });

            var buffer = new byte[4096];
            var msg = new MemoryStream();
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var res = await ws.ReceiveAsync(buffer, _cts.Token);
                if (res.MessageType == WebSocketMessageType.Close) break;
                msg.Write(buffer, 0, res.Count);
                if (msg.Length > 64 * 1024) break; // oversized
                if (!res.EndOfMessage) continue;
                var data = msg.ToArray();
                msg.SetLength(0);

                // Control can be revoked by the technician or disallowed at any time.
                bool stillOwner;
                lock (_controlGate) stillOwner = _controllerId == id && _opt.AllowControl();
                if (owner && !stillOwner)
                {
                    owner = false;
                    lock (_controlGate) { if (_controllerId == id) _controllerId = null; }
                    keyboard.ReleaseAll();
                    await Send(new { t = "revoked" });
                }
                if (!owner)
                {
                    if (TryTakeControl()) { owner = true; await Send(new { t = "hello", control = true, reason = "" }); _host.RemoteEvent($"Remote control granted to {r.Remote}."); }
                    else continue;
                }
                HandleInput(data, keyboard);
            }
        }
        finally
        {
            if (owner)
            {
                lock (_controlGate) { if (_controllerId == id) _controllerId = null; }
                if (keyboard.ReleaseAll()) _host.RemoteKeyboard(KeyboardReport.Empty);
                _host.RemoteReleaseAll();
                _host.RemoteEvent($"Remote control from {r.Remote} ended.");
                ViewersChanged?.Invoke();
            }
            // Complete the close handshake whether we or the browser started it.
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        }
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private void HandleInput(byte[] data, KeyboardState keyboard)
    {
        JsonElement m;
        try { m = JsonDocument.Parse(data).RootElement; } catch (JsonException) { return; }
        if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("t", out var t)) return;
        switch (t.GetString())
        {
            case "k":
                if (m.TryGetProperty("c", out var c) && DomKeyMap.TryMap(c.GetString() ?? "", out var usage))
                {
                    bool down = m.TryGetProperty("d", out var d) && d.ValueKind == JsonValueKind.True;
                    if (down ? keyboard.Press(usage) : keyboard.Release(usage)) _host.RemoteKeyboard(keyboard.ToReport());
                }
                break;
            case "m":
            {
                double x = m.TryGetProperty("x", out var xe) && xe.TryGetDouble(out var xv) ? xv : 0;
                double y = m.TryGetProperty("y", out var ye) && ye.TryGetDouble(out var yv) ? yv : 0;
                int b = m.TryGetProperty("b", out var be) && be.TryGetInt32(out var bv) ? bv : 0;
                int w = m.TryGetProperty("w", out var we) && we.TryGetInt32(out var wv) ? wv : 0;
                _host.RemoteMouse((Ch9329.MouseButtons)(b & 0x07),
                    (int)Math.Round(Math.Clamp(x, 0, 1) * Ch9329Protocol.AbsoluteMax),
                    (int)Math.Round(Math.Clamp(y, 0, 1) * Ch9329Protocol.AbsoluteMax),
                    (sbyte)Math.Clamp(w, -127, 127));
                break;
            }
            case "s":
                if (m.TryGetProperty("id", out var id) && SpecialKeys.Find(id.GetString() ?? "") != null) _host.RemoteSpecialKey(id.GetString()!);
                break;
            case "txt":
                if (m.TryGetProperty("s", out var txt) && txt.GetString() is { Length: > 0 and <= 4000 } text) _host.RemoteText(text);
                break;
            case "r":
                if (keyboard.ReleaseAll()) _host.RemoteKeyboard(KeyboardReport.Empty);
                _host.RemoteReleaseAll();
                break;
        }
    }

    private async Task RespondJsonAsync(Stream s, object o) =>
        await RespondAsync(s, 200, "application/json", JsonSerializer.SerializeToUtf8Bytes(o, WebJson));

    private async Task RespondAsync(Stream s, int status, string contentType, byte[] body, bool html = false)
    {
        string reason = status switch { 200 => "OK", 204 => "No Content", 400 => "Bad Request", 401 => "Unauthorized", 404 => "Not Found", 405 => "Method Not Allowed", 429 => "Too Many Requests", 503 => "Service Unavailable", _ => "Status" };
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n");
        sb.Append("Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nX-Frame-Options: DENY\r\nConnection: close\r\n");
        if (html) sb.Append("Content-Security-Policy: default-src 'self'; img-src 'self' data: blob:; connect-src 'self' wss: ws:; style-src 'unsafe-inline'; script-src 'unsafe-inline'; frame-ancestors 'none'\r\n");
        sb.Append("\r\n");
        await s.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), _cts.Token);
        if (body.Length > 0) await s.WriteAsync(body, _cts.Token);
        await s.FlushAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        foreach (var c in _pinned.Values) c.Dispose();
    }
}
