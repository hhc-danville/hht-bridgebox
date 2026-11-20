namespace HHT.Bridge.Core.Ch9329;

/// <summary>
/// Parsed reply to CMD_GET_INFO (0x01 → 0x81, 8 data bytes).
///   byte 0: chip version (0x30 = V1.0, 0x31 = V1.1 ...)
///   byte 1: USB enumeration status on the target (0x01 = connected and recognised)
///   byte 2: keyboard lock LEDs reported by the target (bit0 Num, bit1 Caps, bit2 Scroll)
/// </summary>
public sealed record Ch9329Info(byte RawVersion, bool TargetUsbConnected, bool NumLock, bool CapsLock, bool ScrollLock)
{
    public string Version => RawVersion >= 0x30 ? $"V{(RawVersion - 0x30) / 10 + 1}.{(RawVersion - 0x30) % 10}" : $"0x{RawVersion:X2}";

    public static bool TryParse(Ch9329Reply reply, out Ch9329Info? info)
    {
        info = null;
        if (reply.BaseCommand != Ch9329Protocol.CmdGetInfo || reply.IsError || reply.Data.Length < 3) return false;
        byte leds = reply.Data[2];
        info = new Ch9329Info(
            reply.Data[0],
            reply.Data[1] == 0x01,
            (leds & 0x01) != 0,
            (leds & 0x02) != 0,
            (leds & 0x04) != 0);
        return true;
    }
}
