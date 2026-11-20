using System.Net.Http.Json;

namespace HHT.Bridge.Core.Remote;

/// <summary>
/// Pushes this bench's snapshot + status to the HHT Bridge cloud relay (server/cloud-relay) every few seconds,
/// so owners can see every bench in every shop from anywhere. View-only: nothing comes back from the cloud.
/// </summary>
public sealed class CloudPublisher : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _publish;
    private readonly Func<CloudSnapshot> _snapshot;
    private CancellationTokenSource? _cts;

    public sealed record CloudSnapshot(string BenchId, string Name, string? Ticket, bool Video, bool TargetUsb, int Viewers, byte[]? Jpeg);

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(5);
    public string? LastError { get; private set; }
    public DateTime? LastSuccess { get; private set; }
    public int Published { get; private set; }
    public event Action<string?>? StatusChanged;

    public CloudPublisher(string relayBaseUrl, string publishKey, Func<CloudSnapshot> snapshot, HttpMessageHandler? handler = null, bool allowLoopbackHttp = false)
    {
        var u = new Uri(relayBaseUrl.TrimEnd('/') + "/");
        if (u.Scheme != "https" && !(u.IsLoopback && allowLoopbackHttp)) throw new ArgumentException("The cloud relay must be reached over https://");
        if (publishKey.Trim().Length < 16) throw new ArgumentException("Publish key looks too short.");
        _publish = new Uri(u, "api/publish");
        _snapshot = snapshot;
        _http = handler != null ? new HttpClient(handler) : new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.Add("X-HHT-Publish-Key", publishKey.Trim());
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                await PublishOnceAsync(ct).ConfigureAwait(false);
                try { await Task.Delay(Interval, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        });
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    public async Task<bool> PublishOnceAsync(CancellationToken ct = default)
    {
        string? error;
        try
        {
            var s = _snapshot();
            var jpeg = s.Jpeg is { Length: > 0 and < 900_000 } ? s.Jpeg : null;
            using var content = HHT.Bridge.Core.Integrations.JsonBody.Create(new
            {
                bench_id = s.BenchId, name = s.Name, ticket = s.Ticket, video = s.Video, target_usb = s.TargetUsb, viewers = s.Viewers,
                jpeg_base64 = jpeg != null ? Convert.ToBase64String(jpeg) : null,
            });
            using var resp = await _http.PostAsync(_publish, content, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) { error = null; Published++; LastSuccess = DateTime.Now; }
            else if ((int)resp.StatusCode == 429) error = LastError; // too fast / locked out: keep previous state
            else error = (int)resp.StatusCode == 401 ? "Cloud relay rejected the publish key." : $"Cloud relay: HTTP {(int)resp.StatusCode}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch (Exception ex) { error = "Cloud relay unreachable: " + ex.Message; }
        if (error != LastError) { LastError = error; StatusChanged?.Invoke(error); }
        return error == null;
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }
}
