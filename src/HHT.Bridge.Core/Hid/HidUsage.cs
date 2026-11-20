namespace HHT.Bridge.Core.Hid;

/// <summary>USB HID Usage Tables, Keyboard/Keypad page (0x07) — the subset a PC keyboard sends.</summary>
public static class HidUsage
{
    public const byte A = 0x04;            // A..Z = 0x04..0x1D
    public const byte D1 = 0x1E;           // 1..9 = 0x1E..0x26
    public const byte D0 = 0x27;
    public const byte Enter = 0x28;
    public const byte Escape = 0x29;
    public const byte Backspace = 0x2A;
    public const byte Tab = 0x2B;
    public const byte Space = 0x2C;
    public const byte Minus = 0x2D;
    public const byte Equal = 0x2E;
    public const byte LeftBracket = 0x2F;
    public const byte RightBracket = 0x30;
    public const byte Backslash = 0x31;
    public const byte Semicolon = 0x33;
    public const byte Apostrophe = 0x34;
    public const byte Grave = 0x35;
    public const byte Comma = 0x36;
    public const byte Period = 0x37;
    public const byte Slash = 0x38;
    public const byte CapsLock = 0x39;
    public const byte F1 = 0x3A;           // F1..F12 = 0x3A..0x45
    public const byte PrintScreen = 0x46;
    public const byte ScrollLock = 0x47;
    public const byte Pause = 0x48;
    public const byte Insert = 0x49;
    public const byte Home = 0x4A;
    public const byte PageUp = 0x4B;
    public const byte Delete = 0x4C;
    public const byte End = 0x4D;
    public const byte PageDown = 0x4E;
    public const byte RightArrow = 0x4F;
    public const byte LeftArrow = 0x50;
    public const byte DownArrow = 0x51;
    public const byte UpArrow = 0x52;
    public const byte NumLock = 0x53;
    public const byte KpDivide = 0x54;
    public const byte KpMultiply = 0x55;
    public const byte KpMinus = 0x56;
    public const byte KpPlus = 0x57;
    public const byte KpEnter = 0x58;
    public const byte Kp1 = 0x59;          // Kp1..Kp9 = 0x59..0x61
    public const byte Kp0 = 0x62;
    public const byte KpDecimal = 0x63;
    public const byte NonUsBackslash = 0x64;
    public const byte Application = 0x65;
    public const byte F13 = 0x68;          // F13..F24 = 0x68..0x73

    public const byte LeftCtrl = 0xE0;
    public const byte LeftShift = 0xE1;
    public const byte LeftAlt = 0xE2;
    public const byte LeftGui = 0xE3;
    public const byte RightCtrl = 0xE4;
    public const byte RightShift = 0xE5;
    public const byte RightAlt = 0xE6;
    public const byte RightGui = 0xE7;

    public static byte Letter(char c) => (byte)(A + (char.ToUpperInvariant(c) - 'A'));
    public static byte Function(int n) => n switch
    {
        >= 1 and <= 12 => (byte)(F1 + n - 1),
        >= 13 and <= 24 => (byte)(F13 + n - 13),
        _ => throw new ArgumentOutOfRangeException(nameof(n)),
    };

    public static bool IsModifier(byte usage) => usage is >= LeftCtrl and <= RightGui;

    /// <summary>Modifier usages 0xE0..0xE7 map to report bits 0..7 in the same order.</summary>
    public static Ch9329.KeyModifiers ModifierBit(byte usage) =>
        IsModifier(usage) ? (Ch9329.KeyModifiers)(1 << (usage - LeftCtrl)) : Ch9329.KeyModifiers.None;
}
