namespace HHT.Bridge.Core.Ch9329;

/// <summary>
/// Frame builder for the WCH CH9329 UART-to-USB-HID bridge chip.
///
/// Frame layout (all commands and replies):
///   HEAD (0x57 0xAB) | ADDR | CMD | LEN | DATA[LEN] | SUM
/// SUM is the low 8 bits of the sum of every preceding byte (HEAD through DATA).
/// Replies set CMD | 0x80 on success and CMD | 0xC0 on error.
/// </summary>
public static class Ch9329Protocol
{
    public const byte Head1 = 0x57;
    public const byte Head2 = 0xAB;
    public const byte DefaultAddress = 0x00;

    public const byte CmdGetInfo = 0x01;
    public const byte CmdSendKeyboard = 0x02;
    public const byte CmdSendMedia = 0x03;
    public const byte CmdSendMouseAbsolute = 0x04;
    public const byte CmdSendMouseRelative = 0x05;
    public const byte CmdReset = 0x0F;

    public const byte ReplySuccessFlag = 0x80;
    public const byte ReplyErrorFlag = 0xC0;

    /// <summary>CH9329 absolute mouse coordinate range is 0..4095 on both axes.</summary>
    public const int AbsoluteMax = 4095;

    public static byte Checksum(ReadOnlySpan<byte> bytes)
    {
        int sum = 0;
        foreach (var b in bytes) sum += b;
        return (byte)(sum & 0xFF);
    }

    public static byte[] BuildFrame(byte command, ReadOnlySpan<byte> data, byte address = DefaultAddress)
    {
        if (data.Length > 64) throw new ArgumentOutOfRangeException(nameof(data), "CH9329 payload is limited to 64 bytes.");
        var frame = new byte[5 + data.Length + 1];
        frame[0] = Head1;
        frame[1] = Head2;
        frame[2] = address;
        frame[3] = command;
        frame[4] = (byte)data.Length;
        data.CopyTo(frame.AsSpan(5));
        frame[^1] = Checksum(frame.AsSpan(0, frame.Length - 1));
        return frame;
    }

    public static byte[] GetInfo(byte address = DefaultAddress) => BuildFrame(CmdGetInfo, ReadOnlySpan<byte>.Empty, address);

    public static byte[] Reset(byte address = DefaultAddress) => BuildFrame(CmdReset, ReadOnlySpan<byte>.Empty, address);

    /// <summary>Standard 8-byte boot keyboard report: modifiers, reserved, 6 key usages.</summary>
    public static byte[] Keyboard(KeyboardReport report, byte address = DefaultAddress)
    {
        Span<byte> data = stackalloc byte[8];
        data[0] = (byte)report.Modifiers;
        data[1] = 0x00;
        for (int i = 0; i < 6; i++) data[2 + i] = report.Keys[i];
        return BuildFrame(CmdSendKeyboard, data, address);
    }

    /// <summary>Absolute mouse: 0x02, buttons, X lo, X hi, Y lo, Y hi, wheel. X/Y are 0..4095.</summary>
    public static byte[] MouseAbsolute(MouseButtons buttons, int x, int y, sbyte wheel, byte address = DefaultAddress)
    {
        x = Math.Clamp(x, 0, AbsoluteMax);
        y = Math.Clamp(y, 0, AbsoluteMax);
        Span<byte> data = stackalloc byte[7];
        data[0] = 0x02;
        data[1] = (byte)buttons;
        data[2] = (byte)(x & 0xFF);
        data[3] = (byte)(x >> 8);
        data[4] = (byte)(y & 0xFF);
        data[5] = (byte)(y >> 8);
        data[6] = unchecked((byte)wheel);
        return BuildFrame(CmdSendMouseAbsolute, data, address);
    }

    /// <summary>Relative mouse: 0x01, buttons, dX, dY, wheel. Deltas are signed bytes (-127..127).</summary>
    public static byte[] MouseRelative(MouseButtons buttons, sbyte dx, sbyte dy, sbyte wheel, byte address = DefaultAddress)
    {
        Span<byte> data = stackalloc byte[5];
        data[0] = 0x01;
        data[1] = (byte)buttons;
        data[2] = unchecked((byte)dx);
        data[3] = unchecked((byte)dy);
        data[4] = unchecked((byte)wheel);
        return BuildFrame(CmdSendMouseRelative, data, address);
    }
}

[Flags]
public enum MouseButtons : byte
{
    None = 0,
    Left = 0x01,
    Right = 0x02,
    Middle = 0x04,
}

[Flags]
public enum KeyModifiers : byte
{
    None = 0,
    LeftCtrl = 0x01,
    LeftShift = 0x02,
    LeftAlt = 0x04,
    LeftGui = 0x08,
    RightCtrl = 0x10,
    RightShift = 0x20,
    RightAlt = 0x40,
    RightGui = 0x80,
}

/// <summary>Immutable snapshot of an 8-byte HID boot keyboard report.</summary>
public sealed class KeyboardReport : IEquatable<KeyboardReport>
{
    public static readonly KeyboardReport Empty = new(KeyModifiers.None, ReadOnlySpan<byte>.Empty);

    public KeyModifiers Modifiers { get; }
    public byte[] Keys { get; }

    public KeyboardReport(KeyModifiers modifiers, ReadOnlySpan<byte> keys)
    {
        if (keys.Length > 6) throw new ArgumentOutOfRangeException(nameof(keys), "A boot keyboard report holds at most 6 keys.");
        Modifiers = modifiers;
        Keys = new byte[6];
        keys.CopyTo(Keys);
    }

    public bool IsEmpty => Modifiers == KeyModifiers.None && Keys.All(k => k == 0);

    public bool Equals(KeyboardReport? other) =>
        other is not null && Modifiers == other.Modifiers && Keys.AsSpan().SequenceEqual(other.Keys);

    public override bool Equals(object? obj) => Equals(obj as KeyboardReport);
    public override int GetHashCode() => HashCode.Combine(Modifiers, Keys[0], Keys[1], Keys[2], Keys[3], Keys[4], Keys[5]);
    public override string ToString() => $"[{Modifiers}] {string.Join(' ', Keys.Select(k => k.ToString("X2")))}";
}
