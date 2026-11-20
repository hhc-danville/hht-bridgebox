using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using HHT.Bridge.Core.Text;

namespace HHT.Bridge.Core.Integrations;

public sealed record AiFinding(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("evidence")] string? Evidence,
    [property: JsonPropertyName("root_cause")] string? RootCause,
    [property: JsonPropertyName("severity")] string? Severity,
    [property: JsonPropertyName("recommendation")] string? Recommendation);

public sealed record AiAnalysis(
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("overall_severity")] string? OverallSeverity,
    [property: JsonPropertyName("findings")] IReadOnlyList<AiFinding> Findings);

/// <summary>
/// Talks to the existing HHC AI backend on the droplet (the one behind HHC-TechTool's AI Technician tab):
///   GET  {base}/health                 header X-HHC-Key
///   POST {base}/analyze  {telemetry}   → {summary, overall_severity, findings[]}
/// For the Bridge Box the "telemetry" is what the technician is looking at: OCR text of the target's screen,
/// findings, the ticket's issue and recent console events. The backend's automated action_ids refer to the
/// technician's own PC, so the console shows recommendations only and never executes anything.
/// No changes to the droplet are needed.
/// </summary>
public sealed class AiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _base;

    public AiClient(string baseUrl, string gateKey, HttpMessageHandler? handler = null, bool allowLoopbackHttp = false)
    {
        var u = new Uri(baseUrl.TrimEnd('/') + "/");
        if (u.Scheme != "https" && !(u.IsLoopback && allowLoopbackHttp)) throw new ArgumentException("The AI backend must be reached over https://");
        _base = u;
        _http = handler != null ? new HttpClient(handler) : new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(120);
        if (!string.IsNullOrWhiteSpace(gateKey)) _http.DefaultRequestHeaders.Add("X-HHC-Key", gateKey.Trim());
    }

    public async Task<bool> HealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var r = await _http.GetAsync(new Uri(_base, "health"), ct).ConfigureAwait(false);
            return r.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
    }

    public static object BuildTelemetry(string? screenText, IEnumerable<Finding> findings, string? ticketIssue, string? device,
        string? biosBrand, IEnumerable<string> recentEvents, string? technicianQuestion) => new
    {
        source = "HHT Bridge Box",
        context = "The customer PC is viewed through an HDMI capture card and controlled with an external USB keyboard/mouse. " +
                  "No agent runs on it; it may be in BIOS/UEFI, a boot menu, Windows Setup/Recovery, or failing to boot. " +
                  "Recommend concrete steps the technician performs by hand (keys to press, menu items, what to check). " +
                  "Automated action_ids do not apply to this PC: use manual_review.",
        technician_question = technicianQuestion,
        ticket_issue = ticketIssue,
        device,
        bios_brand = biosBrand,
        screen_text = screenText is { Length: > 12000 } ? screenText[..12000] : screenText,
        screen_findings = findings.Select(f => new { kind = f.Kind.ToString(), value = f.Value, seen_as = f.SourceLine }).Take(50).ToArray(),
        recent_events = recentEvents.TakeLast(30).ToArray(),
    };

    public async Task<AiAnalysis> AnalyzeAsync(object telemetry, CancellationToken ct = default)
    {
        using var content = JsonBody.Create(new { telemetry });
        using var resp = await _http.PostAsync(new Uri(_base, "analyze"), content, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            string detail = text;
            try { using var d = JsonDocument.Parse(text); if (d.RootElement.TryGetProperty("detail", out var x)) detail = x.ToString(); } catch (JsonException) { }
            throw new HttpRequestException($"AI backend: HTTP {(int)resp.StatusCode} {detail}".Trim(), null, resp.StatusCode);
        }
        var a = JsonSerializer.Deserialize<AiAnalysis>(text) ?? throw new InvalidDataException("Empty AI response.");
        return a with { Findings = a.Findings ?? Array.Empty<AiFinding>() };
    }

    public void Dispose() => _http.Dispose();
}
