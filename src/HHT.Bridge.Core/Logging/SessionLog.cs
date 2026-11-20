using System.Text;

namespace HHT.Bridge.Core.Logging;

public sealed record LogEntry(DateTime Time, string Level, string Message)
{
    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss.fff} [{Level}] {Message}";
}

/// <summary>
/// Append-only technician session log (one file per console session).
/// Records connections, special-key sends, screenshots and errors — never individual
/// keystrokes or pasted text, which may contain customer passwords.
/// </summary>
public sealed class SessionLog : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private readonly List<LogEntry> _entries = new();
    private bool _disposed;

    public string FilePath { get; }
    public DateTime Started { get; }
    public string? Technician { get; }
    public string? Ticket { get; }
    public event Action<string>? EntryWritten;

    public SessionLog(string directory, string? technician = null, string? ticket = null)
    {
        Directory.CreateDirectory(directory);
        Started = DateTime.Now;
        Technician = technician ?? Environment.UserName;
        Ticket = ticket;
        var suffix = string.IsNullOrEmpty(ticket) ? "" : $"-ticket-{ticket}";
        FilePath = Path.Combine(directory, $"session-{Started:yyyyMMdd-HHmmss}{suffix}.log");
        _writer = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
        Write("SESSION", $"Session started. Machine={Environment.MachineName} User={Technician}" +
                         (string.IsNullOrEmpty(ticket) ? "" : $" Ticket={ticket}"));
    }

    /// <summary>Last 500 lines as text.</summary>
    public IReadOnlyList<string> Recent { get { lock (_gate) return _entries.TakeLast(500).Select(e => e.ToString()).ToArray(); } }

    /// <summary>Every entry of this session (for reports).</summary>
    public IReadOnlyList<LogEntry> Entries { get { lock (_gate) return _entries.ToArray(); } }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);
    public void Audit(string message) => Write("AUDIT", message);

    public void Write(string level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        var line = entry.ToString();
        lock (_gate)
        {
            if (_disposed) return;
            _writer.WriteLine(line);
            _entries.Add(entry);
            if (_entries.Count > 20000) _entries.RemoveRange(0, 1000);
        }
        EntryWritten?.Invoke(line);
    }

    public void Dispose()
    {
        Write("SESSION", "Session ended.");
        lock (_gate)
        {
            _disposed = true;
            _writer.Dispose();
        }
    }
}
