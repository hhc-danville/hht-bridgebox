using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HHT.Bridge.Core.Logging;

namespace HHT.Bridge.Core.Integrations;

// Implements the HHC-RMM ticket API (v1) exactly as built on rmm.hammerhillcomputers.com:
//   GET  /api/v1/ping, /api/v1/meta, /api/v1/tickets/{n}
//   POST /api/v1/tickets/{n}/notes | attachments | status | session
// Auth: per-tech key "Authorization: Bearer hht_...". Errors: {"ok":false,"error":code,"message":...}.

public sealed record RmmCustomer(string? Name, string? Phone, string? Email, string? Company);
public sealed record RmmDevice(string? Label, string? Hostname, string? AssetTag, string? Serial, string? DeviceId);

public sealed record RmmTicket(
    string TicketNumber, string Status, RmmCustomer? Customer, RmmDevice? Device, string? Issue, string? Subject,
    string? Priority, string? AssignedTech, DateTime? Created, int NotesCount)
{
    public string Summary =>
        $"{TicketNumber} · {Status}" + (Customer?.Name is { } n ? $" · {n}" : "") + (Device?.Label is { } d ? $" · {d}" : "");
}

public sealed record RmmPing(bool Ok, string? Server, string? Tech, string? Scope, bool CanWrite, int ApiVersion);

public sealed record RmmNote(
    string Category, string Severity, string Action, string? Detail, DateTime Time, string ClientEntryId,
    string Source = "HHT-Bridge", string ToolVersion = RmmClient.ToolVersion);

public sealed class RmmApiException : Exception
{
    public HttpStatusCode Status { get; }
    public string? Code { get; }
    public RmmApiException(HttpStatusCode status, string? code, string message) : base(message) { Status = status; Code = code; }
    /// <summary>Worth retrying later (network, rate limit, server, storage) rather than a permanent rejection.</summary>
    public bool IsTransient => (int)Status is 0 or 429 or >= 500;
}

public sealed class RmmClient : IDisposable
{
    public const string ToolVersion = "bridge-0.4";
    public static readonly string[] Categories = { "info", "diagnostic", "repair", "cleanup", "recovery", "destructive", "network", "security" };
    public static readonly string[] Severities = { "info", "warning", "critical" };
    public static readonly string[] Statuses = { "open", "in_progress", "waiting", "resolved" };
    public const int MaxAttachmentBytes = 25 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly HttpClient _http;
    private readonly Uri _api;

    /// <param name="allowLoopbackHttp">Tests only: allow http://127.0.0.1.</param>
    public RmmClient(string baseUrl, string apiKey, HttpMessageHandler? handler = null, bool allowLoopbackHttp = false)
    {
        var u = new Uri(baseUrl.TrimEnd('/') + "/");
        bool loopback = u.IsLoopback && allowLoopbackHttp;
        if (u.Scheme != "https" && !loopback) throw new ArgumentException("The RMM must be reached over https://");
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("API key required (RMM > My account > API keys).");
        _api = new Uri(u, "api/v1/");
        _http = handler != null ? new HttpClient(handler) : new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HHT-Bridge-Console/0.4");
    }

    /// <summary>The server accepts "T-00012", "t12", "12", "00012". We only strip junk and refuse anything else.</summary>
    public static bool TryNormalizeTicket(string input, out string ticket)
    {
        ticket = (input ?? "").Trim();
        if (!Regex.IsMatch(ticket, @"^[Tt]?-?\d{1,9}$")) return false;
        ticket = ticket.ToUpperInvariant();
        return true;
    }

    public Task<RmmPing> PingAsync(CancellationToken ct = default) => GetAsync<RmmPing>("ping", ct);

    public Task<RmmTicket> GetTicketAsync(string ticket, CancellationToken ct = default) =>
        GetAsync<RmmTicket>("tickets/" + Uri.EscapeDataString(Ticket(ticket)), ct);

    public async Task<long> PostNoteAsync(string ticket, RmmNote note, CancellationToken ct = default)
    {
        var body = new
        {
            category = note.Category, severity = note.Severity, action = Trim(note.Action, 300), detail = Trim(note.Detail, 20000),
            time = note.Time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"), source = note.Source, tool_version = note.ToolVersion,
            client_entry_id = note.ClientEntryId,
        };
        var r = await PostAsync<JsonElement>($"tickets/{Uri.EscapeDataString(Ticket(ticket))}/notes", body, ct).ConfigureAwait(false);
        return r.GetProperty("note_id").GetInt64();
    }

    public async Task<(long Id, string? Url)> AttachAsync(string ticket, string filename, string kind, string contentType, byte[] data,
        bool customerVisible = false, CancellationToken ct = default)
    {
        if (data.Length > MaxAttachmentBytes) throw new RmmApiException(HttpStatusCode.RequestEntityTooLarge, "too_large", $"{filename} is larger than 25 MB.");
        var body = new
        {
            filename, kind, content_type = contentType, data_base64 = Convert.ToBase64String(data), source = "HHT-Bridge",
            visibility = customerVisible ? "customer" : "internal",
        };
        var r = await PostAsync<JsonElement>($"tickets/{Uri.EscapeDataString(Ticket(ticket))}/attachments", body, ct).ConfigureAwait(false);
        return (r.GetProperty("attachment_id").GetInt64(), r.TryGetProperty("url", out var u) ? u.GetString() : null);
    }

    public async Task<string> SetStatusAsync(string ticket, string status, string? summary = null, bool summaryToCustomer = false, CancellationToken ct = default)
    {
        if (!Statuses.Contains(status)) throw new ArgumentException($"Unknown status '{status}'.");
        var body = new { status, summary, summary_visibility = summaryToCustomer ? "customer" : "internal" };
        var r = await PostAsync<JsonElement>($"tickets/{Uri.EscapeDataString(Ticket(ticket))}/status", body, ct).ConfigureAwait(false);
        return r.GetProperty("status").GetString() ?? status;
    }

    public sealed record SessionResult(int NotesCreated, int Duplicates, long? AttachmentId, string? Status);

    /// <summary>Posts the whole session (notes + report + optional final status) in one call. Safe to re-send.</summary>
    public async Task<SessionResult> PostSessionAsync(string ticket, DateTime started, DateTime ended, IReadOnlyList<RmmNote> entries,
        byte[]? reportHtml, string? reportFilename, string? finalStatus, bool reportToCustomer = false, CancellationToken ct = default)
    {
        if (finalStatus != null && !Statuses.Contains(finalStatus)) throw new ArgumentException($"Unknown status '{finalStatus}'.");
        var body = new
        {
            tool_version = ToolVersion,
            started = started.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ended = ended.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            entries = entries.Take(1000).Select(n => new
            {
                category = n.Category, severity = n.Severity, action = Trim(n.Action, 300), detail = Trim(n.Detail, 20000),
                time = n.Time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"), source = n.Source, tool_version = n.ToolVersion,
                client_entry_id = n.ClientEntryId,
            }).ToArray(),
            report_base64 = reportHtml != null ? Convert.ToBase64String(reportHtml) : null,
            report_filename = reportFilename,
            report_kind = reportHtml != null ? "service_report" : null,
            report_visibility = reportHtml != null ? (reportToCustomer ? "customer" : "internal") : null,
            final_status = finalStatus,
        };
        var r = await PostAsync<JsonElement>($"tickets/{Uri.EscapeDataString(Ticket(ticket))}/session", body, ct).ConfigureAwait(false);
        return new SessionResult(
            r.TryGetProperty("notes_created", out var nc) ? nc.GetInt32() : 0,
            r.TryGetProperty("duplicates", out var d) ? d.GetInt32() : 0,
            r.TryGetProperty("attachment_id", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt64() : null,
            r.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null);
    }

    /// <summary>
    /// Turns the console's audit log into ticket work-log entries. Keystrokes/typed text are never in the log,
    /// so nothing sensitive can leak here. Client entry ids are derived from the log line so re-posting is idempotent.
    /// </summary>
    public static IReadOnlyList<RmmNote> NotesFromLog(IEnumerable<LogEntry> entries, string sessionId)
    {
        var notes = new List<RmmNote>();
        foreach (var e in entries)
        {
            if (e.Level is not ("AUDIT" or "WARN" or "ERROR")) continue;
            var m = e.Message;
            string category = "diagnostic", severity = e.Level == "AUDIT" ? "info" : "warning";
            if (Regex.IsMatch(m, @"Forced power off|Reset pressed|Safe Mode|reboot|CHKDSK|DISM|SFC", RegexOptions.IgnoreCase)) category = "repair";
            if (Regex.IsMatch(m, @"Forced power off|Safe Mode on|bcdedit", RegexOptions.IgnoreCase)) severity = "warning";
            if (Regex.IsMatch(m, @"Virtual media|mounted|uploaded", RegexOptions.IgnoreCase)) category = "recovery";
            if (Regex.IsMatch(m, @"Remote (control|viewer)|access key|Sharing", RegexOptions.IgnoreCase)) category = "security";
            if (Regex.IsMatch(m, @"SMART|No bootable|BitLocker|stop code", RegexOptions.IgnoreCase)) severity = "warning";
            var id = DeterministicId(sessionId, e);
            notes.Add(new RmmNote(category, severity, Trim(m, 300)!, m.Length > 300 ? m : null, e.Time, id));
        }
        return notes;
    }

    private static string DeterministicId(string sessionId, LogEntry e)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{sessionId}|{e.Time:O}|{e.Level}|{e.Message}"));
        return new Guid(hash.AsSpan(0, 16)).ToString();
    }

    private static string Ticket(string t) =>
        TryNormalizeTicket(t, out var n) ? n : throw new ArgumentException($"'{t}' is not a ticket number (e.g. T-00012).");

    private static string? Trim(string? s, int max) => s == null ? null : s.Length <= max ? s : s[..max];

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { resp = await _http.GetAsync(new Uri(_api, path), ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new RmmApiException(0, "network", "Cannot reach the RMM: " + ex.Message); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new RmmApiException(0, "timeout", "The RMM did not answer in time."); }
        using (resp) return await Read<T>(resp, ct).ConfigureAwait(false);
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { using var content = JsonBody.Create(body, Json); resp = await _http.PostAsync(new Uri(_api, path), content, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new RmmApiException(0, "network", "Cannot reach the RMM: " + ex.Message); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new RmmApiException(0, "timeout", "The RMM did not answer in time."); }
        using (resp) return await Read<T>(resp, ct).ConfigureAwait(false);
    }

    private static async Task<T> Read<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            string? code = null, message = null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                code = doc.RootElement.TryGetProperty("error", out var c) ? c.GetString() : null;
                message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
            }
            catch (JsonException) { }
            throw new RmmApiException(resp.StatusCode, code, message ?? $"RMM returned HTTP {(int)resp.StatusCode}.");
        }
        return JsonSerializer.Deserialize<T>(text, Json) ?? throw new RmmApiException(resp.StatusCode, "bad_json", "Empty response from the RMM.");
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Notes that could not be posted (no internet at a customer site, RMM down) are kept on disk and
/// re-sent later with the same client_entry_id, which the RMM de-duplicates.
/// </summary>
public sealed class RmmOfflineQueue
{
    private readonly string _path;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public sealed record Item(string Ticket, RmmNote Note);

    public RmmOfflineQueue(string path) => _path = path;

    public IReadOnlyList<Item> Items
    {
        get
        {
            lock (_gate)
            {
                try { return File.Exists(_path) ? JsonSerializer.Deserialize<List<Item>>(File.ReadAllText(_path), Json) ?? new() : new(); }
                catch (JsonException) { return new List<Item>(); }
            }
        }
    }

    public void Add(string ticket, RmmNote note)
    {
        lock (_gate)
        {
            var list = Items.ToList();
            if (list.Any(i => i.Note.ClientEntryId == note.ClientEntryId)) return;
            list.Add(new Item(ticket, note));
            Save(list);
        }
    }

    /// <summary>Posts queued notes; keeps those that fail transiently, drops permanent rejections. Returns (sent, kept, dropped).</summary>
    public async Task<(int Sent, int Kept, int Dropped)> FlushAsync(RmmClient client, Action<string>? log = null, CancellationToken ct = default)
    {
        var pending = Items.ToList();
        var keep = new List<Item>();
        int sent = 0, dropped = 0;
        foreach (var item in pending)
        {
            try { await client.PostNoteAsync(item.Ticket, item.Note, ct).ConfigureAwait(false); sent++; }
            catch (RmmApiException ex) when (ex.IsTransient) { keep.Add(item); }
            catch (RmmApiException ex) { dropped++; log?.Invoke($"Queued note for {item.Ticket} rejected: {ex.Message}"); }
        }
        lock (_gate) Save(keep);
        return (sent, keep.Count, dropped);
    }

    private void Save(List<Item> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(list, Json));
        File.Move(_path + ".tmp", _path, overwrite: true);
    }
}
