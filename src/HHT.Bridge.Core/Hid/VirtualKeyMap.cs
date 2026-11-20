namespace HHT.Bridge.Core.Hid;

/// <summary>
/// Maps Windows virtual-key codes (as delivered by a WH_KEYBOARD_LL hook, which reports
/// left/right-specific modifier VKs and an "extended" flag) to HID keyboard usages.
///
/// Mapping is positional (the physical key), so the target's own keyboard layout decides
/// which character appears — the same as plugging a real USB keyboard into it.
/// </summary>
public static class VirtualKeyMap
{
    private static readonly Dictionary<int, byte> Map = Build();

    // VKs whose non-extended form comes from the numeric keypad (NumLock off).
    private static readonly Dictionary<int, byte> KeypadNavigation = new()
    {
        [0x2D] = HidUsage.Kp0,      // VK_INSERT
        [0x2E] = HidUsage.KpDecimal,// VK_DELETE
        [0x23] = HidUsage.Kp1,      // VK_END
        [0x28] = 0x5A,              // VK_DOWN  -> Kp2
        [0x22] = 0x5B,              // VK_NEXT  -> Kp3
        [0x25] = 0x5C,              // VK_LEFT  -> Kp4
        [0x0C] = 0x5D,              // VK_CLEAR -> Kp5
        [0x27] = 0x5E,              // VK_RIGHT -> Kp6
        [0x24] = 0x5F,              // VK_HOME  -> Kp7
        [0x26] = 0x60,              // VK_UP    -> Kp8
        [0x21] = 0x61,              // VK_PRIOR -> Kp9
    };

    public static bool TryMap(int vk, bool extended, out byte usage)
    {
        if (!extended && KeypadNavigation.TryGetValue(vk, out usage)) return true;
        if (vk == 0x0D && extended) { usage = HidUsage.KpEnter; return true; }
        // Right Ctrl / Right Alt arrive as extended generic VKs from some sources.
        if (vk == 0x11) { usage = extended ? HidUsage.RightCtrl : HidUsage.LeftCtrl; return true; }
        if (vk == 0x12) { usage = extended ? HidUsage.RightAlt : HidUsage.LeftAlt; return true; }
        return Map.TryGetValue(vk, out usage);
    }

    private static Dictionary<int, byte> Build()
    {
        var m = new Dictionary<int, byte>();
        for (int i = 0; i < 26; i++) m[0x41 + i] = (byte)(HidUsage.A + i);         // A..Z
        for (int i = 1; i <= 9; i++) m[0x30 + i] = (byte)(HidUsage.D1 + i - 1);    // 1..9
        m[0x30] = HidUsage.D0;
        for (int i = 0; i < 12; i++) m[0x70 + i] = (byte)(HidUsage.F1 + i);        // F1..F12
        for (int i = 0; i < 12; i++) m[0x7C + i] = (byte)(HidUsage.F13 + i);       // F13..F24
        for (int i = 1; i <= 9; i++) m[0x60 + i] = (byte)(HidUsage.Kp1 + i - 1);   // VK_NUMPAD1..9
        m[0x60] = HidUsage.Kp0;

        m[0x0D] = HidUsage.Enter;
        m[0x1B] = HidUsage.Escape;
        m[0x08] = HidUsage.Backspace;
        m[0x09] = HidUsage.Tab;
        m[0x20] = HidUsage.Space;
        m[0xBD] = HidUsage.Minus;        // VK_OEM_MINUS
        m[0xBB] = HidUsage.Equal;        // VK_OEM_PLUS
        m[0xDB] = HidUsage.LeftBracket;  // VK_OEM_4
        m[0xDD] = HidUsage.RightBracket; // VK_OEM_6
        m[0xDC] = HidUsage.Backslash;    // VK_OEM_5
        m[0xBA] = HidUsage.Semicolon;    // VK_OEM_1
        m[0xDE] = HidUsage.Apostrophe;   // VK_OEM_7
        m[0xC0] = HidUsage.Grave;        // VK_OEM_3
        m[0xBC] = HidUsage.Comma;
        m[0xBE] = HidUsage.Period;
        m[0xBF] = HidUsage.Slash;        // VK_OEM_2
        m[0xE2] = HidUsage.NonUsBackslash; // VK_OEM_102
        m[0x14] = HidUsage.CapsLock;
        m[0x2C] = HidUsage.PrintScreen;  // VK_SNAPSHOT
        m[0x91] = HidUsage.ScrollLock;
        m[0x13] = HidUsage.Pause;
        m[0x2D] = HidUsage.Insert;
        m[0x24] = HidUsage.Home;
        m[0x21] = HidUsage.PageUp;
        m[0x2E] = HidUsage.Delete;
        m[0x23] = HidUsage.End;
        m[0x22] = HidUsage.PageDown;
        m[0x27] = HidUsage.RightArrow;
        m[0x25] = HidUsage.LeftArrow;
        m[0x28] = HidUsage.DownArrow;
        m[0x26] = HidUsage.UpArrow;
        m[0x90] = HidUsage.NumLock;
        m[0x6F] = HidUsage.KpDivide;
        m[0x6A] = HidUsage.KpMultiply;
        m[0x6D] = HidUsage.KpMinus;
        m[0x6B] = HidUsage.KpPlus;
        m[0x6E] = HidUsage.KpDecimal;    // VK_DECIMAL
        m[0x0C] = 0x5D;                  // VK_CLEAR (keypad 5, NumLock off)
        m[0x5D] = HidUsage.Application;  // VK_APPS

        m[0xA2] = HidUsage.LeftCtrl;
        m[0xA3] = HidUsage.RightCtrl;
        m[0xA0] = HidUsage.LeftShift;
        m[0xA1] = HidUsage.RightShift;
        m[0x10] = HidUsage.LeftShift;    // generic VK_SHIFT
        m[0xA4] = HidUsage.LeftAlt;
        m[0xA5] = HidUsage.RightAlt;
        m[0x5B] = HidUsage.LeftGui;
        m[0x5C] = HidUsage.RightGui;
        return m;
    }
}
