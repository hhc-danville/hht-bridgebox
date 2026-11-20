using HHT.Bridge.Core.Ch9329;
using HHT.Bridge.Core.Hid;

namespace HHT.Bridge.Core.Automation;

/// <summary>
/// Parses human-readable key chords such as "Ctrl+Alt+Del", "Win+R", "Shift+F10", "Enter", "F12"
/// into a keyboard report (press). Release is always <see cref="KeyboardReport.Empty"/>.
/// </summary>
public static class KeyChord
{
    private static readonly Dictionary<string, KeyModifiers> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = KeyModifiers.LeftCtrl, ["control"] = KeyModifiers.LeftCtrl,
        ["shift"] = KeyModifiers.LeftShift,
        ["alt"] = KeyModifiers.LeftAlt,
        ["win"] = KeyModifiers.LeftGui, ["windows"] = KeyModifiers.LeftGui, ["gui"] = KeyModifiers.LeftGui, ["cmd"] = KeyModifiers.LeftGui,
        ["rctrl"] = KeyModifiers.RightCtrl, ["rshift"] = KeyModifiers.RightShift, ["ralt"] = KeyModifiers.RightAlt, ["altgr"] = KeyModifiers.RightAlt,
    };

    private static readonly Dictionary<string, byte> Keys = BuildKeys();

    public static bool TryParse(string chord, out KeyboardReport report)
    {
        report = KeyboardReport.Empty;
        if (string.IsNullOrWhiteSpace(chord)) return false;
        var mods = KeyModifiers.None;
        var keys = new List<byte>();
        foreach (var raw in chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (Modifiers.TryGetValue(raw, out var m)) { mods |= m; continue; }
            if (Keys.TryGetValue(raw, out var u)) { if (keys.Count < 6) keys.Add(u); continue; }
            return false;
        }
        if (mods == KeyModifiers.None && keys.Count == 0) return false;
        report = new KeyboardReport(mods, keys.ToArray());
        return true;
    }

    public static KeyboardReport Parse(string chord) =>
        TryParse(chord, out var r) ? r : throw new FormatException($"Unknown key chord '{chord}'.");

    private static Dictionary<string, byte> BuildKeys()
    {
        var k = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        for (char c = 'A'; c <= 'Z'; c++) k[c.ToString()] = HidUsage.Letter(c);
        for (char c = '1'; c <= '9'; c++) k[c.ToString()] = (byte)(HidUsage.D1 + (c - '1'));
        k["0"] = HidUsage.D0;
        for (int i = 1; i <= 24; i++) k["F" + i] = HidUsage.Function(i);
        void Add(byte u, params string[] names) { foreach (var n in names) k[n] = u; }
        Add(HidUsage.Enter, "Enter", "Return");
        Add(HidUsage.Escape, "Esc", "Escape");
        Add(HidUsage.Backspace, "Backspace", "Bksp");
        Add(HidUsage.Tab, "Tab");
        Add(HidUsage.Space, "Space");
        Add(HidUsage.Delete, "Del", "Delete");
        Add(HidUsage.Insert, "Ins", "Insert");
        Add(HidUsage.Home, "Home");
        Add(HidUsage.End, "End");
        Add(HidUsage.PageUp, "PgUp", "PageUp");
        Add(HidUsage.PageDown, "PgDn", "PageDown");
        Add(HidUsage.UpArrow, "Up");
        Add(HidUsage.DownArrow, "Down");
        Add(HidUsage.LeftArrow, "Left");
        Add(HidUsage.RightArrow, "Right");
        Add(HidUsage.PrintScreen, "PrtSc", "PrintScreen");
        Add(HidUsage.Pause, "Pause", "Break");
        Add(HidUsage.CapsLock, "CapsLock");
        Add(HidUsage.NumLock, "NumLock");
        Add(HidUsage.ScrollLock, "ScrollLock");
        Add(HidUsage.Application, "Menu", "Apps");
        Add(HidUsage.Minus, "Minus");
        Add(HidUsage.Equal, "Equal", "Equals");
        Add(HidUsage.Comma, "Comma");
        Add(HidUsage.Period, "Period");
        Add(HidUsage.Slash, "Slash");
        return k;
    }
}
