using System.Text.Json;
using System.Text.RegularExpressions;

namespace HHT.Bridge.Core.Text;

public sealed record ScreenSnapshot(string Label, DateTime Time, string Text, string? ScreenshotPath = null);

public enum SettingChangeKind { Changed, Added, Removed }

public sealed record SettingChange(SettingChangeKind Kind, string Setting, string? Before, string? After)
{
    public override string ToString() => Kind switch
    {
        SettingChangeKind.Changed => $"{Setting}: {Before} → {After}",
        SettingChangeKind.Added => $"{Setting}: (new) {After}",
        _ => $"{Setting}: {Before} (no longer shown)",
    };
}

/// <summary>
/// Compares two OCR reads of the same BIOS page ("before" at check-in, "after" at pickup) and lists settings
/// whose values changed. BIOS pages show "Setting   [Value]" or "Setting      Value" rows; OCR noise such
/// as clocks and temperatures is ignored.
/// </summary>
public static class ScreenCompare
{
    private static readonly Regex Bracketed = new(@"^(?<k>[A-Za-z][^\[\]]{2,60}?)\s*\[(?<v>[^\]]{1,40})\]", RegexOptions.Compiled);
    private static readonly Regex Spaced = new(@"^(?<k>[A-Za-z][A-Za-z0-9 ()/#&.\-]{2,60}?)\s{2,}(?<v>\S.{0,40})$", RegexOptions.Compiled);
    private static readonly Regex Colon = new(@"^(?<k>[A-Za-z][A-Za-z0-9 ()/#&.\-]{2,60}?)\s*:\s*(?<v>\S.{0,40})$", RegexOptions.Compiled);
    private static readonly Regex Volatile = new(@"time|date|temp|fan|speed|voltage|vcore|rpm|°|uptime|clock", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static Dictionary<string, string> ExtractSettings(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            var m = Bracketed.Match(line);
            if (!m.Success) m = Spaced.Match(line);
            if (!m.Success) m = Colon.Match(line);
            if (!m.Success) continue;
            var key = Regex.Replace(m.Groups["k"].Value.Trim(), @"\s+", " ");
            var val = Regex.Replace(m.Groups["v"].Value.Trim(), @"\s+", " ");
            if (Volatile.IsMatch(key)) continue;
            d.TryAdd(key, val);
        }
        return d;
    }

    public static IReadOnlyList<SettingChange> Compare(ScreenSnapshot before, ScreenSnapshot after)
    {
        var b = ExtractSettings(before.Text);
        var a = ExtractSettings(after.Text);
        var changes = new List<SettingChange>();
        foreach (var (k, v) in b)
        {
            if (!a.TryGetValue(k, out var av)) changes.Add(new(SettingChangeKind.Removed, k, v, null));
            else if (!string.Equals(Norm(v), Norm(av), StringComparison.OrdinalIgnoreCase)) changes.Add(new(SettingChangeKind.Changed, k, v, av));
        }
        foreach (var (k, v) in a)
            if (!b.ContainsKey(k)) changes.Add(new(SettingChangeKind.Added, k, null, v));
        return changes;
    }

    // OCR often confuses O/0 and l/1 and adds stray punctuation; compare a normalised form.
    private static string Norm(string v) => Regex.Replace(v, @"[\s.,:;]", "").Replace('0', 'O').Replace('1', 'l').Replace('I', 'l');

    public static void Save(string path, IReadOnlyList<ScreenSnapshot> snaps)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(snaps, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static IReadOnlyList<ScreenSnapshot> Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<ScreenSnapshot>>(File.ReadAllText(path)) ?? new() : new(); }
        catch (JsonException) { return Array.Empty<ScreenSnapshot>(); }
    }
}
