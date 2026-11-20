using HHT.Bridge.Core.Ch9329;

namespace HHT.Bridge.Core.Hid;

/// <summary>
/// Converts text into a sequence of keyboard reports (press, release per character)
/// assuming the TARGET uses a US keyboard layout. Characters with no US-layout key are skipped.
/// </summary>
public static class TextTyper
{
    private static readonly Dictionary<char, (byte Usage, bool Shift)> Map = Build();

    public static bool TryMap(char c, out byte usage, out bool shift)
    {
        if (Map.TryGetValue(c, out var v)) { usage = v.Usage; shift = v.Shift; return true; }
        usage = 0; shift = false; return false;
    }

    public sealed record Result(IReadOnlyList<KeyboardReport> Reports, int TypedCharacters, int SkippedCharacters);

    public static Result ToReports(string text)
    {
        var reports = new List<KeyboardReport>(text.Length * 2);
        int typed = 0, skipped = 0;
        // Treat CRLF as a single Enter.
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var c in text)
        {
            if (!TryMap(c, out var usage, out var shift)) { skipped++; continue; }
            var mods = shift ? KeyModifiers.LeftShift : KeyModifiers.None;
            reports.Add(new KeyboardReport(mods, new[] { usage }));
            reports.Add(KeyboardReport.Empty);
            typed++;
        }
        return new Result(reports, typed, skipped);
    }

    private static Dictionary<char, (byte, bool)> Build()
    {
        var m = new Dictionary<char, (byte, bool)>();
        for (char c = 'a'; c <= 'z'; c++) m[c] = (HidUsage.Letter(c), false);
        for (char c = 'A'; c <= 'Z'; c++) m[c] = (HidUsage.Letter(c), true);
        m['0'] = (HidUsage.D0, false);
        for (char c = '1'; c <= '9'; c++) m[c] = ((byte)(HidUsage.D1 + (c - '1')), false);

        const string shiftedDigits = "!@#$%^&*()"; // Shift+1 .. Shift+0
        for (int i = 0; i < 9; i++) m[shiftedDigits[i]] = ((byte)(HidUsage.D1 + i), true);
        m[')'] = (HidUsage.D0, true);

        void Pair(char plain, char shifted, byte usage) { m[plain] = (usage, false); m[shifted] = (usage, true); }
        Pair('-', '_', HidUsage.Minus);
        Pair('=', '+', HidUsage.Equal);
        Pair('[', '{', HidUsage.LeftBracket);
        Pair(']', '}', HidUsage.RightBracket);
        Pair('\\', '|', HidUsage.Backslash);
        Pair(';', ':', HidUsage.Semicolon);
        Pair('\'', '"', HidUsage.Apostrophe);
        Pair('`', '~', HidUsage.Grave);
        Pair(',', '<', HidUsage.Comma);
        Pair('.', '>', HidUsage.Period);
        Pair('/', '?', HidUsage.Slash);
        m[' '] = (HidUsage.Space, false);
        m['\n'] = (HidUsage.Enter, false);
        m['\t'] = (HidUsage.Tab, false);
        return m;
    }
}
