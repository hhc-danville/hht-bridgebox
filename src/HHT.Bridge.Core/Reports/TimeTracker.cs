using System.Globalization;
using System.Text;

namespace HHT.Bridge.Core.Reports;

public sealed record TimeSummary(TimeSpan Wall, TimeSpan Active, TimeSpan Billable, int IncrementMinutes)
{
    public double BillableHours => Billable.TotalHours;
    public override string ToString() =>
        $"Active {Fmt(Active)} of {Fmt(Wall)} on the bench; billable {Fmt(Billable)} ({BillableHours:0.00} h, {IncrementMinutes}-min increments)";
    private static string Fmt(TimeSpan t) => $"{(int)t.TotalHours}h {t.Minutes:00}m";
}

/// <summary>
/// Billable bench time: counts time between activity marks (control input, macros, key sends, alerts…)
/// but caps each gap at <see cref="IdleCap"/>, so a PC left running a long test overnight isn't billed as labour.
/// </summary>
public sealed class TimeTracker
{
    private readonly object _gate = new();
    private readonly DateTime _start;
    private DateTime? _last;
    private TimeSpan _active;

    public TimeSpan IdleCap { get; init; } = TimeSpan.FromMinutes(5);
    public int IncrementMinutes { get; init; } = 15;
    public TimeSpan MinimumBillable { get; init; } = TimeSpan.Zero;

    public TimeTracker(DateTime start) => _start = start;

    public void Mark(DateTime when)
    {
        lock (_gate)
        {
            if (_last is { } last && when > last)
            {
                var gap = when - last;
                _active += gap < IdleCap ? gap : IdleCap;
            }
            if (_last == null || when > _last) _last = when;
        }
    }

    public TimeSummary Summarize(DateTime now)
    {
        lock (_gate)
        {
            var active = _active;
            var inc = TimeSpan.FromMinutes(Math.Max(1, IncrementMinutes));
            var billable = active <= TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromTicks((long)Math.Ceiling(active.Ticks / (double)inc.Ticks) * inc.Ticks);
            if (billable < MinimumBillable && active > TimeSpan.Zero) billable = MinimumBillable;
            return new TimeSummary(now - _start, active, billable, IncrementMinutes);
        }
    }

    /// <summary>Appends one line per session to a CSV timesheet (Excel-friendly).</summary>
    public static void AppendTimesheet(string csvPath, DateTime start, string? ticket, string technician, TimeSummary s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);
        bool newFile = !File.Exists(csvPath);
        var sb = new StringBuilder();
        if (newFile) sb.AppendLine("date,start,ticket,technician,active_minutes,billable_hours");
        sb.AppendLine(string.Join(",",
            start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), start.ToString("HH:mm", CultureInfo.InvariantCulture),
            Csv(ticket ?? ""), Csv(technician),
            ((int)Math.Round(s.Active.TotalMinutes)).ToString(CultureInfo.InvariantCulture),
            s.BillableHours.ToString("0.00", CultureInfo.InvariantCulture)));
        File.AppendAllText(csvPath, sb.ToString());
    }

    private static string Csv(string v)
    {
        if (v.Length > 0 && "=+-@".Contains(v[0])) v = "'" + v; // block spreadsheet formula injection
        return v.Contains(',') || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
