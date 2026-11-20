using HHT.Bridge.Core.Ch9329;

namespace HHT.Bridge.Core.Automation;

public sealed record BiosProfile(string Brand, string[] SetupKeys, string[] BootMenuKeys, string Notes = "");

public static class BiosProfiles
{
    public static readonly IReadOnlyList<BiosProfile> All = new[]
    {
        new BiosProfile("Generic / unknown", new[] { "Del", "F2" }, new[] { "F12", "F11", "F8", "Esc" }, "Alternates the common keys."),
        new BiosProfile("Dell", new[] { "F2" }, new[] { "F12" }),
        new BiosProfile("HP", new[] { "F10" }, new[] { "F9" }, "Esc opens the HP Startup Menu."),
        new BiosProfile("Lenovo ThinkPad / ThinkCentre", new[] { "F1" }, new[] { "F12" }, "Enter at the splash opens the startup interrupt menu."),
        new BiosProfile("Lenovo IdeaPad / Legion", new[] { "F2" }, new[] { "F12" }, "Some models need the Novo button."),
        new BiosProfile("ASUS", new[] { "Del", "F2" }, new[] { "F8", "Esc" }),
        new BiosProfile("Acer", new[] { "F2", "Del" }, new[] { "F12" }, "F12 boot menu may need enabling in setup."),
        new BiosProfile("MSI", new[] { "Del" }, new[] { "F11" }),
        new BiosProfile("Gigabyte / AORUS", new[] { "Del" }, new[] { "F12" }),
        new BiosProfile("ASRock", new[] { "F2", "Del" }, new[] { "F11" }),
        new BiosProfile("Toshiba / Dynabook", new[] { "F2" }, new[] { "F12" }),
        new BiosProfile("Samsung", new[] { "F2" }, new[] { "F10" }),
        new BiosProfile("Intel NUC", new[] { "F2" }, new[] { "F10" }),
        new BiosProfile("Supermicro", new[] { "Del" }, new[] { "F11" }),
        new BiosProfile("Microsoft Surface", Array.Empty<string>(), Array.Empty<string>(), "Uses the Volume Up button at power-on, not a keyboard key; use 'Reboot into BIOS/UEFI setup' from Windows instead."),
    };

    public static BiosProfile? Find(string brand) => All.FirstOrDefault(p => p.Brand.Equals(brand, StringComparison.OrdinalIgnoreCase));
}

public enum BiosTarget { Setup, BootMenu }

/// <summary>
/// Repeatedly taps the brand's setup or boot-menu key (cycling if there are several) until the
/// firmware screen is detected, the time limit passes, or the technician cancels.
/// Start it, then power on / restart the target.
/// </summary>
public sealed class BiosEntrySequencer
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(150);
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan DetectEvery { get; init; } = TimeSpan.FromSeconds(1.5);

    public sealed record Result(bool Detected, int KeyPresses, TimeSpan Elapsed, string Message);

    /// <param name="detectFirmware">Returns true when the firmware screen is visible (OCR). May be null.</param>
    public async Task<Result> RunAsync(BiosProfile profile, BiosTarget target, Action<KeyboardReport> send,
        Func<CancellationToken, Task<bool>>? detectFirmware, CancellationToken ct = default)
    {
        var keys = target == BiosTarget.Setup ? profile.SetupKeys : profile.BootMenuKeys;
        if (keys.Length == 0) return new Result(false, 0, TimeSpan.Zero, profile.Notes.Length > 0 ? profile.Notes : "No key for this brand.");
        var reports = keys.Select(KeyChord.Parse).ToArray();
        var start = DateTime.UtcNow;
        var lastDetect = start;
        int presses = 0;
        Task<bool>? detecting = null;
        try
        {
            while (DateTime.UtcNow - start < MaxDuration)
            {
                ct.ThrowIfCancellationRequested();
                send(reports[presses % reports.Length]);
                await Task.Delay(40, ct);
                send(KeyboardReport.Empty);
                presses++;

                if (detecting is { IsCompleted: true })
                {
                    if (detecting.Result)
                        return new Result(true, presses, DateTime.UtcNow - start, "Firmware screen detected.");
                    detecting = null;
                }
                if (detectFirmware != null && detecting == null && DateTime.UtcNow - lastDetect >= DetectEvery)
                {
                    lastDetect = DateTime.UtcNow;
                    detecting = detectFirmware(ct);
                }
                await Task.Delay(Interval, ct);
            }
            return new Result(false, presses, DateTime.UtcNow - start, "Time limit reached without detecting the firmware screen.");
        }
        catch (OperationCanceledException)
        {
            send(KeyboardReport.Empty);
            return new Result(false, presses, DateTime.UtcNow - start, "Stopped.");
        }
    }
}
