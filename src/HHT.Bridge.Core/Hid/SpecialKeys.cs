using HHT.Bridge.Core.Ch9329;

namespace HHT.Bridge.Core.Hid;

/// <summary>
/// Key combinations the technician laptop's own OS would intercept (Ctrl+Alt+Del, Win, Alt+Tab)
/// or that are handy at boot (F2/F12/Del for setup and boot menus).
/// </summary>
public sealed record SpecialKey(string Id, string Label, KeyModifiers Modifiers, byte Usage, string Hint = "")
{
    /// <summary>Press then release: two reports.</summary>
    public IReadOnlyList<KeyboardReport> ToReports() => new[]
    {
        new KeyboardReport(Modifiers, Usage == 0 ? ReadOnlySpan<byte>.Empty : new[] { Usage }),
        KeyboardReport.Empty,
    };
}

public static class SpecialKeys
{
    public static readonly SpecialKey CtrlAltDel = new("ctrl-alt-del", "Ctrl+Alt+Del", KeyModifiers.LeftCtrl | KeyModifiers.LeftAlt, HidUsage.Delete, "Sign-in / reboot");
    public static readonly SpecialKey CtrlShiftEsc = new("ctrl-shift-esc", "Ctrl+Shift+Esc", KeyModifiers.LeftCtrl | KeyModifiers.LeftShift, HidUsage.Escape, "Task Manager");
    public static readonly SpecialKey AltTab = new("alt-tab", "Alt+Tab", KeyModifiers.LeftAlt, HidUsage.Tab);
    public static readonly SpecialKey AltF4 = new("alt-f4", "Alt+F4", KeyModifiers.LeftAlt, HidUsage.Function(4));
    public static readonly SpecialKey Win = new("win", "Win", KeyModifiers.LeftGui, 0, "Start menu");
    public static readonly SpecialKey WinR = new("win-r", "Win+R", KeyModifiers.LeftGui, HidUsage.Letter('R'), "Run");
    public static readonly SpecialKey WinX = new("win-x", "Win+X", KeyModifiers.LeftGui, HidUsage.Letter('X'), "Power user menu");
    public static readonly SpecialKey Escape = new("esc", "Esc", KeyModifiers.None, HidUsage.Escape);
    public static readonly SpecialKey Delete = new("del", "Del", KeyModifiers.None, HidUsage.Delete, "BIOS setup (most desktops)");
    public static readonly SpecialKey F2 = new("f2", "F2", KeyModifiers.None, HidUsage.Function(2), "BIOS setup (most laptops)");
    public static readonly SpecialKey F8 = new("f8", "F8", KeyModifiers.None, HidUsage.Function(8), "Boot menu (some boards)");
    public static readonly SpecialKey F10 = new("f10", "F10", KeyModifiers.None, HidUsage.Function(10), "HP setup / save & exit");
    public static readonly SpecialKey F11 = new("f11", "F11", KeyModifiers.None, HidUsage.Function(11), "Boot menu (MSI/ASRock)");
    public static readonly SpecialKey F12 = new("f12", "F12", KeyModifiers.None, HidUsage.Function(12), "Boot menu (Dell/Lenovo)");
    public static readonly SpecialKey PrintScreen = new("prtsc", "PrtSc", KeyModifiers.None, HidUsage.PrintScreen);

    public static readonly IReadOnlyList<SpecialKey> All = new[]
    {
        CtrlAltDel, CtrlShiftEsc, AltTab, AltF4, Win, WinR, WinX, Escape, Delete, F2, F8, F10, F11, F12, PrintScreen,
    };

    public static SpecialKey? Find(string id) => All.FirstOrDefault(k => k.Id == id);
}
