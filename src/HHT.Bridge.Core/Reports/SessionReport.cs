using System.Net;
using System.Text;
using HHT.Bridge.Core.Logging;
using HHT.Bridge.Core.Text;

namespace HHT.Bridge.Core.Reports;

public sealed record ReportScreenshot(DateTime Time, string Path, string Caption);

public sealed class SessionReport
{
    public string? Ticket { get; init; }
    public string Technician { get; init; } = Environment.UserName;
    public string Workstation { get; init; } = Environment.MachineName;
    public DateTime Started { get; init; }
    public DateTime Ended { get; init; } = DateTime.Now;
    public string? ControlDevice { get; init; }
    public string? VideoDevice { get; init; }
    public string? Notes { get; init; }
    public string? RecordingPath { get; init; }
    public IReadOnlyList<LogEntry> Events { get; init; } = Array.Empty<LogEntry>();
    public IReadOnlyList<ReportScreenshot> Screenshots { get; init; } = Array.Empty<ReportScreenshot>();
    public IReadOnlyList<Finding> Findings { get; init; } = Array.Empty<Finding>();
    public TimeSummary? Time { get; init; }
    public IReadOnlyList<SettingChange> BiosChanges { get; init; } = Array.Empty<SettingChange>();
    public IReadOnlyList<string> TestResults { get; init; } = Array.Empty<string>();
    public string? AiSummary { get; init; }
    public IReadOnlyList<string> AiRecommendations { get; init; } = Array.Empty<string>();
    public Remote.SignOff? SignOff { get; init; }
    /// <summary>Customer copy: leaves out the internal activity log and AI notes.</summary>
    public bool CustomerCopy { get; init; }

    public TimeSpan Duration => Ended - Started;
}

/// <summary>
/// Renders a self-contained HTML service report (screenshots embedded) that opens in any browser,
/// prints to PDF, and can be attached to a ticket. Text is HTML-encoded; no external resources.
/// </summary>
public static class SessionReportWriter
{
    public static string ToHtml(SessionReport r, Func<string, byte[]?>? readFile = null)
    {
        readFile ??= p => File.Exists(p) ? File.ReadAllBytes(p) : null;
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder(64 * 1024);
        string title = string.IsNullOrEmpty(r.Ticket) ? "Bridge Box service report" : $"Bridge Box service report — Ticket {r.Ticket}";

        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>").Append(E(title)).Append("</title><style>");
        sb.Append("body{font-family:'Segoe UI',system-ui,sans-serif;color:#1d2228;margin:0;background:#f4f6f9}");
        sb.Append(".page{max-width:960px;margin:0 auto;background:#fff;padding:32px 40px}");
        sb.Append("header{display:flex;align-items:center;gap:14px;border-bottom:3px solid #2f7df6;padding-bottom:14px}");
        sb.Append(".logo{background:#2f7df6;color:#fff;font-weight:700;border-radius:8px;padding:10px 12px}");
        sb.Append("h1{font-size:22px;margin:0}h2{font-size:16px;margin:28px 0 8px;color:#2f7df6;text-transform:uppercase;letter-spacing:.04em}");
        sb.Append("table{border-collapse:collapse;width:100%;font-size:13px}td,th{border-bottom:1px solid #e3e7ee;padding:6px 8px;text-align:left;vertical-align:top}");
        sb.Append("th{background:#f0f3f8;font-weight:600}.muted{color:#6b7480}.err{color:#b42318;font-weight:600}.warn{color:#b26b00;font-weight:600}");
        sb.Append(".shots figure{margin:0 0 18px}.shots img{max-width:100%;border:1px solid #cfd6e0;border-radius:4px}figcaption{font-size:12px;color:#6b7480}");
        sb.Append("@media print{body{background:#fff}.page{padding:0}.shots figure{page-break-inside:avoid}}</style></head><body><div class=\"page\">");

        sb.Append("<header><div class=\"logo\">HHT</div><div><h1>").Append(E(title)).Append("</h1>");
        sb.Append("<div class=\"muted\">Hammer Hill Technologies · HHT Bridge Console</div></div></header>");

        sb.Append("<h2>Session</h2><table>");
        Row(sb, "Ticket", r.Ticket ?? "—");
        Row(sb, "Technician", r.Technician);
        Row(sb, "Workstation", r.Workstation);
        Row(sb, "Started", r.Started.ToString("yyyy-MM-dd HH:mm:ss"));
        Row(sb, "Ended", r.Ended.ToString("yyyy-MM-dd HH:mm:ss"));
        Row(sb, "Duration", $"{(int)r.Duration.TotalHours}h {r.Duration.Minutes}m {r.Duration.Seconds}s");
        if (r.VideoDevice != null) Row(sb, "Video capture", r.VideoDevice);
        if (r.ControlDevice != null) Row(sb, "Keyboard/mouse", r.ControlDevice);
        if (r.RecordingPath != null && !r.CustomerCopy) Row(sb, "Recording", r.RecordingPath);
        if (r.Time != null) Row(sb, "Bench time", r.Time.ToString());
        sb.Append("</table>");

        if (!string.IsNullOrWhiteSpace(r.Notes))
            sb.Append("<h2>Technician notes</h2><p>").Append(E(r.Notes).Replace("\n", "<br>")).Append("</p>");

        if (r.Findings.Count > 0)
        {
            sb.Append("<h2>Findings from the screen</h2><table><tr><th>Item</th><th>Value</th><th>Seen as</th></tr>");
            foreach (var f in r.Findings.OrderBy(f => f.IsProblem ? 0 : 1))
            {
                string cls = f.Kind == FindingKind.Error ? "err" : f.Kind == FindingKind.Warning ? "warn" : "";
                sb.Append("<tr><td class=\"").Append(cls).Append("\">").Append(E(f.Label)).Append("</td><td>")
                  .Append(E(f.Value)).Append("</td><td class=\"muted\">").Append(E(f.SourceLine)).Append("</td></tr>");
            }
            sb.Append("</table>");
        }

        if (r.TestResults.Count > 0)
        {
            sb.Append("<h2>Hardware tests</h2><ul>");
            foreach (var t in r.TestResults) sb.Append("<li>").Append(E(t)).Append("</li>");
            sb.Append("</ul>");
        }

        if (r.BiosChanges.Count > 0)
        {
            sb.Append("<h2>BIOS settings changed (before → after)</h2><table><tr><th>Setting</th><th>Before</th><th>After</th></tr>");
            foreach (var c in r.BiosChanges)
                sb.Append("<tr><td>").Append(E(c.Setting)).Append("</td><td>").Append(E(c.Before ?? "—")).Append("</td><td>").Append(E(c.After ?? "—")).Append("</td></tr>");
            sb.Append("</table>");
        }

        if (!r.CustomerCopy && r.AiSummary != null)
        {
            sb.Append("<h2>AI assistant (internal)</h2><p>").Append(E(r.AiSummary)).Append("</p>");
            if (r.AiRecommendations.Count > 0)
            {
                sb.Append("<ul>");
                foreach (var a in r.AiRecommendations) sb.Append("<li>").Append(E(a)).Append("</li>");
                sb.Append("</ul>");
            }
        }

        var actions = r.CustomerCopy ? new List<LogEntry>() : r.Events.Where(e => e.Level is "AUDIT" or "WARN" or "ERROR" or "SESSION").ToList();
        if (actions.Count > 0) sb.Append("<h2>Activity</h2><table><tr><th style=\"width:90px\">Time</th><th style=\"width:70px\">Type</th><th>Event</th></tr>");
        foreach (var e in actions)
        {
            string cls = e.Level == "ERROR" ? "err" : e.Level == "WARN" ? "warn" : "";
            sb.Append("<tr><td>").Append(e.Time.ToString("HH:mm:ss")).Append("</td><td class=\"").Append(cls).Append("\">")
              .Append(E(e.Level)).Append("</td><td>").Append(E(e.Message)).Append("</td></tr>");
        }
        if (actions.Count > 0) sb.Append("</table>");

        if (r.Screenshots.Count > 0)
        {
            sb.Append("<h2>Screenshots</h2><div class=\"shots\">");
            foreach (var s in r.Screenshots)
            {
                var bytes = readFile(s.Path);
                sb.Append("<figure>");
                if (bytes != null) sb.Append("<img alt=\"screenshot\" src=\"data:image/png;base64,").Append(Convert.ToBase64String(bytes)).Append("\">");
                else sb.Append("<div class=\"muted\">(image missing: ").Append(E(s.Path)).Append(")</div>");
                sb.Append("<figcaption>").Append(s.Time.ToString("HH:mm:ss")).Append(" — ").Append(E(s.Caption)).Append("</figcaption></figure>");
            }
            sb.Append("</div>");
        }

        if (r.SignOff is { } so)
        {
            sb.Append("<h2>Customer sign-off</h2><p>").Append(E(so.AgreedSummary).Replace("\n", "<br>")).Append("</p>");
            sb.Append("<img alt=\"signature\" style=\"max-width:360px;border-bottom:1px solid #1d2228\" src=\"data:image/png;base64,")
              .Append(Convert.ToBase64String(so.SignaturePng)).Append("\"><p>").Append(E(so.Name)).Append(" — ")
              .Append(so.Time.ToString("yyyy-MM-dd HH:mm")).Append("</p>");
        }

        sb.Append("<p class=\"muted\" style=\"margin-top:28px;font-size:12px\">Typed text and keystrokes are never recorded in this report. Generated ")
          .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append(".</p></div></body></html>");
        return sb.ToString();

        static void Row(StringBuilder sb, string k, string v) =>
            sb.Append("<tr><th style=\"width:160px\">").Append(WebUtility.HtmlEncode(k)).Append("</th><td>").Append(WebUtility.HtmlEncode(v)).Append("</td></tr>");
    }
}
