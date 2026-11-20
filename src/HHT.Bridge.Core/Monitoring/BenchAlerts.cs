using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace HHT.Bridge.Core.Monitoring;

public enum AlertKind
{
    /// <summary>The screen has not changed for <see cref="AlertRule.Minutes"/> (job finished or hung).</summary>
    ScreenIdle,
    /// <summary>The screen changed after having been still for at least a minute (e.g. a reboot started).</summary>
    ScreenChanged,
    /// <summary>On-screen text (OCR) matches <see cref="AlertRule.Pattern"/>.</summary>
    TextAppears,
    /// <summary>No video frames for 30 s (target powered off / cable pulled).</summary>
    VideoLost,
}

public sealed record AlertRule(AlertKind Kind, double Minutes = 5, string Pattern = "", bool Enabled = true)
{
    public string Describe() => Kind switch
    {
        AlertKind.ScreenIdle => $"Screen unchanged for {Minutes:0.#} min",
        AlertKind.ScreenChanged => "Screen changes after being still",
        AlertKind.TextAppears => $"Text appears: {Pattern}",
        AlertKind.VideoLost => "Video signal lost",
        _ => Kind.ToString(),
    };
}

public sealed record AlertEvent(AlertRule Rule, DateTime Time, string Message);

/// <summary>
/// Watches a stream of "how much did the picture change" values (0..255 mean absolute difference
/// between successive downscaled grey frames) plus occasional OCR text, and fires each rule once per
/// occurrence. A rule re-arms when its condition clears.
/// </summary>
public sealed class ScreenActivityMonitor
{
    /// <summary>Mean pixel difference above which a frame counts as "changed". Video noise on cheap dongles is ~0.5–2.</summary>
    public double ChangeThreshold { get; init; } = 4.0;
    public TimeSpan VideoLostAfter { get; init; } = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly List<AlertRule> _rules = new();
    private readonly HashSet<AlertRule> _fired = new();
    private DateTime? _lastChange;
    private DateTime? _lastFrame;
    private DateTime? _stillSince;

    public event Action<AlertEvent>? Alert;

    public DateTime? LastChange { get { lock (_gate) return _lastChange; } }

    public void SetRules(IEnumerable<AlertRule> rules)
    {
        lock (_gate)
        {
            _rules.Clear();
            _rules.AddRange(rules.Where(r => r.Enabled));
            _fired.Clear();
        }
    }

    public IReadOnlyList<AlertRule> Rules { get { lock (_gate) return _rules.ToArray(); } }

    /// <summary>Call for every captured frame.</summary>
    public void OnFrame(double diff, DateTime now)
    {
        var events = new List<AlertEvent>();
        lock (_gate)
        {
            _lastFrame = now;
            _lastChange ??= now;
            _stillSince ??= now;
            Rearm(AlertKind.VideoLost);

            if (diff >= ChangeThreshold)
            {
                var stillFor = now - _stillSince.Value;
                _lastChange = now;
                _stillSince = now;
                Rearm(AlertKind.ScreenIdle);
                if (stillFor >= TimeSpan.FromMinutes(1))
                    foreach (var r in _rules.Where(r => r.Kind == AlertKind.ScreenChanged))
                        events.Add(new AlertEvent(r, now, $"Screen changed after {stillFor.TotalMinutes:0.#} min of no activity."));
            }
            else
            {
                var still = now - _lastChange.Value;
                foreach (var r in _rules.Where(r => r.Kind == AlertKind.ScreenIdle))
                    if (still >= TimeSpan.FromMinutes(r.Minutes) && _fired.Add(r))
                        events.Add(new AlertEvent(r, now, $"Screen has not changed for {still.TotalMinutes:0.#} min (finished or stuck?)."));
            }
        }
        Raise(events);
    }

    /// <summary>Call periodically (e.g. once a second) so "video lost" fires even when no frames arrive.</summary>
    public void Tick(DateTime now)
    {
        var events = new List<AlertEvent>();
        lock (_gate)
        {
            if (_lastFrame is { } lf && now - lf >= VideoLostAfter)
                foreach (var r in _rules.Where(r => r.Kind == AlertKind.VideoLost))
                    if (_fired.Add(r)) events.Add(new AlertEvent(r, now, "Video signal lost (no frames)."));
        }
        Raise(events);
    }

    /// <summary>Call with OCR text whenever it is read.</summary>
    public void OnText(string text, DateTime now)
    {
        var events = new List<AlertEvent>();
        lock (_gate)
        {
            foreach (var r in _rules.Where(r => r.Kind == AlertKind.TextAppears && r.Pattern.Length > 0))
            {
                bool match;
                try { match = Regex.IsMatch(text, r.Pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)); }
                catch (ArgumentException) { match = text.Contains(r.Pattern, StringComparison.OrdinalIgnoreCase); }
                catch (RegexMatchTimeoutException) { match = false; }
                if (match) { if (_fired.Add(r)) events.Add(new AlertEvent(r, now, $"\"{r.Pattern}\" appeared on screen.")); }
                else _fired.Remove(r);
            }
        }
        Raise(events);
    }

    private void Rearm(AlertKind kind) => _fired.RemoveWhere(r => r.Kind == kind);

    private void Raise(List<AlertEvent> events)
    {
        foreach (var e in events) Alert?.Invoke(e);
    }
}

/// <summary>
/// Sends alerts to a phone. Works with ntfy (self-hosted on the droplet or ntfy.sh: plain-text POST
/// with Title/Priority/Tags headers) and any webhook that accepts a POST body.
/// </summary>
public sealed class AlertNotifier
{
    private readonly HttpClient _http;
    public string? Url { get; set; }
    public string? BearerToken { get; set; }

    public AlertNotifier(HttpClient? http = null) => _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

    public bool IsConfigured => Uri.TryCreate(Url, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http");

    public async Task<(bool Ok, string Detail)> SendAsync(string title, string message, bool urgent = false, CancellationToken ct = default)
    {
        if (!IsConfigured) return (false, "No notification URL configured.");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Url) { Content = new StringContent(message, Encoding.UTF8, "text/plain") };
            req.Headers.TryAddWithoutValidation("Title", Ascii(title));
            req.Headers.TryAddWithoutValidation("Priority", urgent ? "high" : "default");
            req.Headers.TryAddWithoutValidation("Tags", "computer");
            if (!string.IsNullOrEmpty(BearerToken)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", BearerToken);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            return (resp.IsSuccessStatusCode, $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // HTTP headers must be ASCII.
    private static string Ascii(string s) => new(s.Select(c => c < 128 && !char.IsControl(c) ? c : '-').ToArray());
}
