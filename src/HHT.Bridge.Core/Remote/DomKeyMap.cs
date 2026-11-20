using HHT.Bridge.Core.Hid;

namespace HHT.Bridge.Core.Remote;

/// <summary>
/// Maps browser KeyboardEvent.code values (physical key positions, layout independent) to HID usages,
/// so the remote web viewer behaves like a real keyboard plugged into the target.
/// </summary>
public static class DomKeyMap
{
    private static readonly Dictionary<string, byte> Map = Build();

    public static bool TryMap(string code, out byte usage) => Map.TryGetValue(code ?? "", out usage);

    private static Dictionary<string, byte> Build()
    {
        var m = new Dictionary<string, byte>(StringComparer.Ordinal);
        for (char c = 'A'; c <= 'Z'; c++) m["Key" + c] = HidUsage.Letter(c);
        for (int i = 1; i <= 9; i++) m["Digit" + i] = (byte)(HidUsage.D1 + i - 1);
        m["Digit0"] = HidUsage.D0;
        for (int i = 1; i <= 24; i++) m["F" + i] = HidUsage.Function(i);
        for (int i = 1; i <= 9; i++) m["Numpad" + i] = (byte)(HidUsage.Kp1 + i - 1);
        m["Numpad0"] = HidUsage.Kp0;
        m["Enter"] = HidUsage.Enter; m["NumpadEnter"] = HidUsage.KpEnter;
        m["Escape"] = HidUsage.Escape; m["Backspace"] = HidUsage.Backspace; m["Tab"] = HidUsage.Tab; m["Space"] = HidUsage.Space;
        m["Minus"] = HidUsage.Minus; m["Equal"] = HidUsage.Equal; m["BracketLeft"] = HidUsage.LeftBracket; m["BracketRight"] = HidUsage.RightBracket;
        m["Backslash"] = HidUsage.Backslash; m["Semicolon"] = HidUsage.Semicolon; m["Quote"] = HidUsage.Apostrophe; m["Backquote"] = HidUsage.Grave;
        m["Comma"] = HidUsage.Comma; m["Period"] = HidUsage.Period; m["Slash"] = HidUsage.Slash; m["IntlBackslash"] = HidUsage.NonUsBackslash;
        m["CapsLock"] = HidUsage.CapsLock; m["PrintScreen"] = HidUsage.PrintScreen; m["ScrollLock"] = HidUsage.ScrollLock; m["Pause"] = HidUsage.Pause;
        m["Insert"] = HidUsage.Insert; m["Home"] = HidUsage.Home; m["PageUp"] = HidUsage.PageUp; m["Delete"] = HidUsage.Delete;
        m["End"] = HidUsage.End; m["PageDown"] = HidUsage.PageDown;
        m["ArrowRight"] = HidUsage.RightArrow; m["ArrowLeft"] = HidUsage.LeftArrow; m["ArrowDown"] = HidUsage.DownArrow; m["ArrowUp"] = HidUsage.UpArrow;
        m["NumLock"] = HidUsage.NumLock; m["NumpadDivide"] = HidUsage.KpDivide; m["NumpadMultiply"] = HidUsage.KpMultiply;
        m["NumpadSubtract"] = HidUsage.KpMinus; m["NumpadAdd"] = HidUsage.KpPlus; m["NumpadDecimal"] = HidUsage.KpDecimal;
        m["ContextMenu"] = HidUsage.Application;
        m["ControlLeft"] = HidUsage.LeftCtrl; m["ShiftLeft"] = HidUsage.LeftShift; m["AltLeft"] = HidUsage.LeftAlt; m["MetaLeft"] = HidUsage.LeftGui;
        m["ControlRight"] = HidUsage.RightCtrl; m["ShiftRight"] = HidUsage.RightShift; m["AltRight"] = HidUsage.RightAlt; m["MetaRight"] = HidUsage.RightGui;
        m["OSLeft"] = HidUsage.LeftGui; m["OSRight"] = HidUsage.RightGui;
        return m;
    }
}
