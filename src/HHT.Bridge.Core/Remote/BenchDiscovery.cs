using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace HHT.Bridge.Core.Remote;

/// <summary>UDP beacon each console broadcasts on the shop LAN. Contains no secrets (no access key).</summary>
public sealed record BenchBeacon(string Id, string Name, int Port, string Fingerprint, string? Ticket, bool Video, bool TargetUsb, int Viewers)
{
    public const string App = "hht-bridge";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Wire(string App, int V, string Id, string Name, int Port, string Fp, string? Ticket, bool Video, bool TargetUsb, int Viewers);

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(new Wire(App, 1, Id, Name, Port, Fingerprint, Ticket, Video, TargetUsb, Viewers), Json);

    public static bool TryParse(ReadOnlySpan<byte> data, out BenchBeacon? beacon)
    {
        beacon = null;
        if (data.Length is 0 or > 2048) return false;
        try
        {
            var w = JsonSerializer.Deserialize<Wire>(data, Json);
            if (w is null || w.App != App || w.V != 1 || string.IsNullOrWhiteSpace(w.Id) || w.Port is <= 0 or > 65535) return false;
            string name = (w.Name ?? "Bench").Trim();
            beacon = new BenchBeacon(w.Id, name.Length > 40 ? name[..40] : name, w.Port, w.Fp ?? "", w.Ticket, w.Video, w.TargetUsb, w.Viewers);
            return true;
        }
        catch (JsonException) { return false; }
    }
}

public sealed record BenchInfo(string Id, string Name, string Address, int Port, string Fingerprint, string? Ticket,
    bool Video, bool TargetUsb, int Viewers, DateTime LastSeen, bool IsSelf);

/// <summary>Benches heard from recently (plus this console itself).</summary>
public sealed class BenchDirectory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, BenchInfo> _benches = new();
    public TimeSpan Expiry { get; init; } = TimeSpan.FromSeconds(15);
    public Func<BenchInfo>? Self { get; set; }

    public void Upsert(BenchBeacon b, string address, DateTime now)
    {
        lock (_gate)
            _benches[b.Id] = new BenchInfo(b.Id, b.Name, address, b.Port, b.Fingerprint, b.Ticket, b.Video, b.TargetUsb, b.Viewers, now, false);
    }

    public IReadOnlyList<BenchInfo> List(DateTime now)
    {
        var list = new List<BenchInfo>();
        var self = Self?.Invoke();
        if (self != null) list.Add(self);
        lock (_gate)
        {
            foreach (var key in _benches.Where(kv => now - kv.Value.LastSeen > Expiry).Select(kv => kv.Key).ToList()) _benches.Remove(key);
            list.AddRange(_benches.Values.Where(b => self == null || b.Id != self.Id).OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase));
        }
        return list;
    }

    public BenchInfo? Find(string id, DateTime now) => List(now).FirstOrDefault(b => b.Id == id);
}

/// <summary>Sends this console's beacon every few seconds and listens for others (UDP broadcast).</summary>
public sealed class BenchDiscovery : IDisposable
{
    public const int DefaultPort = 47811;
    private readonly BenchDirectory _directory;
    private readonly Func<BenchBeacon> _self;
    private readonly int _port;
    private readonly IPEndPoint[] _targets;
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(3);

    /// <param name="extraTargets">Additional unicast targets (tests, or benches on another subnet).</param>
    public BenchDiscovery(BenchDirectory directory, Func<BenchBeacon> self, int port = DefaultPort, IEnumerable<IPEndPoint>? extraTargets = null, bool broadcast = true)
    {
        _directory = directory;
        _self = self;
        _port = port;
        var t = new List<IPEndPoint>();
        if (broadcast) t.Add(new IPEndPoint(IPAddress.Broadcast, port));
        if (extraTargets != null) t.AddRange(extraTargets);
        _targets = t.ToArray();
    }

    public void Start()
    {
        var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.EnableBroadcast = true;
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
        _udp = udp;
        _ = Task.Run(ReceiveLoop);
        _ = Task.Run(SendLoop);
    }

    private async Task SendLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var bytes = _self().ToBytes();
                foreach (var t in _targets)
                    try { await _udp!.SendAsync(bytes, t, _cts.Token); } catch (SocketException) { }
                await Task.Delay(Interval, _cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
        }
    }

    private async Task ReceiveLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await _udp!.ReceiveAsync(_cts.Token);
                if (!BenchBeacon.TryParse(r.Buffer, out var b) || b == null) continue;
                if (b.Id == _self().Id) continue;
                _directory.Upsert(b, r.RemoteEndPoint.Address.ToString(), DateTime.UtcNow);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { await Task.Delay(500); }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Dispose();
    }
}

/// <summary>Self-signed TLS certificate for the console's HTTPS server, and SHA-256 pinning helpers.</summary>
public static class TlsCertificates
{
    public static string Fingerprint(X509Certificate cert) => Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));

    public static X509Certificate2 CreateSelfSigned(string commonName)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={commonName}, O=Hammer Hill Technologies", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(commonName);
        san.AddDnsName("localhost");
        try { san.AddDnsName(Dns.GetHostName()); } catch (SocketException) { }
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        // Round-trip through PKCS#12 so the private key is usable by SChannel (Windows) as well as OpenSSL.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>Loads the console certificate, creating and saving it on first use.
    /// <paramref name="protect"/>/<paramref name="unprotect"/> wrap the PFX at rest (Windows DPAPI in the console).</summary>
    public static X509Certificate2 LoadOrCreate(string path, string commonName, Func<byte[], byte[]>? protect = null, Func<byte[], byte[]>? unprotect = null)
    {
        try
        {
            if (File.Exists(path))
            {
                var data = File.ReadAllBytes(path);
                if (unprotect != null) data = unprotect(data);
                var c = X509CertificateLoader.LoadPkcs12(data, null, X509KeyStorageFlags.Exportable);
                if (c.NotAfter > DateTime.Now.AddDays(30)) return c;
            }
        }
        catch (Exception) { /* unreadable or other user: regenerate */ }
        var cert = CreateSelfSigned(commonName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pfx = cert.Export(X509ContentType.Pkcs12);
        File.WriteAllBytes(path, protect != null ? protect(pfx) : pfx);
        return cert;
    }

    /// <summary>HttpClient that trusts exactly one certificate (by SHA-256 fingerprint) — used to fetch other benches' snapshots.</summary>
    public static HttpClient PinnedClient(string fingerprint, TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                cert != null && string.Equals(Fingerprint(cert), fingerprint, StringComparison.OrdinalIgnoreCase),
        };
        return new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(4) };
    }

    public static string NewAccessKey()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I
        var bytes = RandomNumberGenerator.GetBytes(16);
        var chars = bytes.Select(b => alphabet[b % alphabet.Length]).ToArray();
        return $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}-{new string(chars, 12, 4)}";
    }
}
