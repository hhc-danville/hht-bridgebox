using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using HHT.Bridge.Core.Remote;

namespace HHT.Bridge.Core.Hardware;

public sealed record VmImage(string Name, long Size);

public sealed record VmStatus(
    [property: JsonPropertyName("mounted")] string? Mounted,
    [property: JsonPropertyName("mode")] string? Mode,
    [property: JsonPropertyName("images")] IReadOnlyList<VmImage> Images,
    [property: JsonPropertyName("free")] long FreeBytes,
    [property: JsonPropertyName("gadget")] bool GadgetActive,
    [property: JsonPropertyName("version")] string Version);

/// <summary>
/// Client for the Bridge Box virtual-media module (firmware/virtual-media: a Raspberry Pi in USB-gadget
/// mode that presents an ISO as a USB CD-ROM, or an .img as a USB disk, to the customer's PC).
/// HTTPS with the module's self-signed certificate pinned by SHA-256 fingerprint, plus a bearer token.
/// </summary>
public sealed class VirtualMediaClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly Uri _base;

    public VirtualMediaClient(string baseUrl, string token, string fingerprint)
    {
        _base = new Uri(baseUrl.TrimEnd('/') + "/");
        if (_base.Scheme != "https") throw new ArgumentException("The virtual media module must be reached over https://");
        var fp = fingerprint.Replace(":", "").Replace(" ", "").Trim();
        if (fp.Length != 64) throw new ArgumentException("Certificate fingerprint must be the 64-hex-digit SHA-256 shown by the module installer.");
        _http = TlsCertificates.PinnedClient(fp, TimeSpan.FromSeconds(30));
        _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan; // uploads are long; per-call timeouts below
        _http.DefaultRequestHeaders.Add("X-HHT-Token", token);
    }

    /// <summary>Test-only constructor (plain HttpClient).</summary>
    public VirtualMediaClient(HttpClient http, string baseUrl, string token)
    {
        _http = http;
        _base = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Add("X-HHT-Token", token);
    }

    public static bool IsAllowedImageName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 120 && name == Path.GetFileName(name) && !name.StartsWith('.')
        && (name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
        && name.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ' ' or '(' or ')');

    public async Task<VmStatus> StatusAsync(CancellationToken ct = default)
    {
        using var cts = WithTimeout(ct, 10);
        return await _http.GetFromJsonAsync<VmStatus>(new Uri(_base, "status"), Json, cts.Token).ConfigureAwait(false)
               ?? throw new InvalidDataException("Empty status.");
    }

    public async Task UploadAsync(string localPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var name = Path.GetFileName(localPath);
        if (!IsAllowedImageName(name)) throw new ArgumentException("Only .iso and .img files with plain names can be uploaded.");
        await using var file = File.OpenRead(localPath);
        using var content = new ProgressStreamContent(file, file.Length, progress);
        content.Headers.ContentLength = file.Length;
        using var resp = await _http.PutAsync(new Uri(_base, "images/" + Uri.EscapeDataString(name)), content, ct).ConfigureAwait(false);
        await Ensure(resp).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        using var cts = WithTimeout(ct, 15);
        using var resp = await _http.DeleteAsync(new Uri(_base, "images/" + Uri.EscapeDataString(name)), cts.Token).ConfigureAwait(false);
        await Ensure(resp).ConfigureAwait(false);
    }

    /// <param name="asCdrom">true: read-only CD-ROM (ISO installers). false: removable USB disk (.img).</param>
    public async Task MountAsync(string name, bool asCdrom, CancellationToken ct = default)
    {
        using var cts = WithTimeout(ct, 15);
        var url = $"mount?name={Uri.EscapeDataString(name)}&mode={(asCdrom ? "cdrom" : "disk")}";
        using var resp = await _http.PostAsync(new Uri(_base, url), null, cts.Token).ConfigureAwait(false);
        await Ensure(resp).ConfigureAwait(false);
    }

    public async Task EjectAsync(CancellationToken ct = default)
    {
        using var cts = WithTimeout(ct, 15);
        using var resp = await _http.PostAsync(new Uri(_base, "eject"), null, cts.Token).ConfigureAwait(false);
        await Ensure(resp).ConfigureAwait(false);
    }

    private static CancellationTokenSource WithTimeout(CancellationToken ct, int seconds)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        return cts;
    }

    private static async Task Ensure(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        throw new HttpRequestException($"Virtual media module: {(int)resp.StatusCode} {body.Trim()}", null, resp.StatusCode);
    }

    public void Dispose() => _http.Dispose();

    private sealed class ProgressStreamContent : HttpContent
    {
        private readonly Stream _src;
        private readonly long _length;
        private readonly IProgress<double>? _progress;
        public ProgressStreamContent(Stream src, long length, IProgress<double>? progress) { _src = src; _length = length; _progress = progress; }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            var buf = new byte[1 << 20];
            long sent = 0;
            int n;
            while ((n = await _src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await stream.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                sent += n;
                _progress?.Report(_length == 0 ? 1 : (double)sent / _length);
            }
        }

        protected override bool TryComputeLength(out long length) { length = _length; return true; }
    }
}
