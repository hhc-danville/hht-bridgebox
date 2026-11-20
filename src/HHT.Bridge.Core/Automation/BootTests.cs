using System.Text.RegularExpressions;

namespace HHT.Bridge.Core.Automation;

/// <summary>
/// A hands-off hardware test: boot an ISO from the virtual-media module, wait for the result on screen,
/// screenshot it and report PASS/FAIL. The ISO itself is supplied by the shop (upload it once in the Hardware tab).
/// </summary>
public sealed record BootTestPlan(string Id, string Name, string ImageName, string PassRegex, string FailRegex, int MaxMinutes, string Notes,
    string BootDeviceRegex = @"HHT|Virtual\s*Media|Linux\s*File|CD|DVD|USB|UEFI.*(File|Gadget)");

public static class BootTestPlans
{
    public static readonly IReadOnlyList<BootTestPlan> BuiltIn = new[]
    {
        new BootTestPlan("memtest86plus", "RAM test (Memtest86+)", "memtest86plus.iso",
            PassRegex: @"pass\s*complete,?\s*no\s*errors|Pass:\s*[1-9]\d*.*Errors:\s*0\b",
            FailRegex: @"Errors:\s*[1-9]|\bFAIL(ED)?\b|unexpected\s*interrupt",
            MaxMinutes: 180, Notes: "Memtest86+ (GPL) from memtest.org. One full pass takes 20–90 min depending on RAM size."),
        new BootTestPlan("memtest86", "RAM test (PassMark MemTest86 Free)", "memtest86-usb.img",
            PassRegex: @"test\s*result:?\s*PASS|\bPASS\b(?!.*FAIL)", FailRegex: @"test\s*result:?\s*FAIL|\bFAIL\b",
            MaxMinutes: 240, Notes: "MemTest86 Free (PassMark) USB image, mounted as a USB disk. Runs 4 passes by default."),
        new BootTestPlan("custom", "Custom ISO", "", PassRegex: @"\bPASS(ED)?\b", FailRegex: @"\bFAIL(ED)?\b", MaxMinutes: 120,
            Notes: "Pick any uploaded image; edit the pass/fail text to match what your tool prints."),
    };
}

public enum BootTestResult { Pass, Fail, Timeout, Aborted, SetupFailed }

public sealed record BootTestOutcome(BootTestResult Result, string Message, TimeSpan Duration, string? LastScreenText);

/// <summary>What the runner needs from the bench (implemented by the console; faked in tests).</summary>
public interface IBenchRig
{
    Task<bool> MountAsync(string imageName, bool asCdrom, CancellationToken ct);
    /// <summary>Restart the target (controller reset/power, or a prompt to the technician). False if impossible.</summary>
    Task<bool> RestartTargetAsync(CancellationToken ct);
    Task<bool> OpenBootMenuAsync(CancellationToken ct);
    Task<string?> ReadScreenAsync(CancellationToken ct);
    void SendKey(string chord);
    Task ScreenshotAsync(string caption);
    void Log(string message);
}

public static class BootMenuNavigator
{
    /// <summary>
    /// Given OCR lines of a one-time boot menu (top item highlighted), returns how many Down presses reach the first
    /// line matching <paramref name="device"/>, or null if no line matches. Title/help lines are skipped.
    /// </summary>
    public static int? DownPressesTo(IReadOnlyList<string> lines, Regex device)
    {
        var items = lines.Select(l => l.Trim())
            .Where(l => l.Length >= 3 && !Regex.IsMatch(l, @"^(please\s+select|boot\s*(menu|manager|option\s*menu)|startup\s*menu|select\s+boot|use\s+the|press|enter\s*setup|esc|↑|↓|\^|v\s*and)", RegexOptions.IgnoreCase))
            .ToList();
        int idx = items.FindIndex(l => device.IsMatch(l));
        return idx < 0 ? null : idx;
    }
}

public sealed class BootTestRunner
{
    public TimeSpan PollEvery { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan BootMenuSettle { get; init; } = TimeSpan.FromSeconds(3);

    public async Task<BootTestOutcome> RunAsync(BootTestPlan plan, string imageName, bool asCdrom, IBenchRig rig,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var start = DateTime.UtcNow;
        string? last = null;
        var pass = new Regex(plan.PassRegex, RegexOptions.IgnoreCase);
        var fail = new Regex(plan.FailRegex, RegexOptions.IgnoreCase);
        void Say(string m) { progress?.Report(m); rig.Log($"Test {plan.Name}: {m}"); }
        try
        {
            Say($"Mounting {imageName}...");
            if (!await rig.MountAsync(imageName, asCdrom, ct)) return new(BootTestResult.SetupFailed, "Could not mount the image on the virtual-media module.", DateTime.UtcNow - start, null);

            Say("Restarting the customer PC...");
            if (!await rig.RestartTargetAsync(ct)) return new(BootTestResult.SetupFailed, "Could not restart the target.", DateTime.UtcNow - start, null);

            Say("Opening the boot menu...");
            if (!await rig.OpenBootMenuAsync(ct)) return new(BootTestResult.SetupFailed, "Boot menu not detected. Select the HHT device manually, then restart the test with 'already booted'.", DateTime.UtcNow - start, null);
            await Task.Delay(BootMenuSettle, ct);

            last = await rig.ReadScreenAsync(ct);
            var presses = last == null ? null : BootMenuNavigator.DownPressesTo(last.Split('\n'), new Regex(plan.BootDeviceRegex, RegexOptions.IgnoreCase));
            if (presses == null) return new(BootTestResult.SetupFailed, "The virtual-media device was not found in the boot menu (check UEFI/Legacy and Secure Boot).", DateTime.UtcNow - start, last);
            for (int i = 0; i < presses; i++) { rig.SendKey("Down"); await Task.Delay(150, ct); }
            rig.SendKey("Enter");
            Say($"Booting the test image ({presses} down)...");

            return await WatchAsync(plan, rig, pass, fail, start, progress, ct);
        }
        catch (OperationCanceledException)
        {
            return new(BootTestResult.Aborted, "Stopped by technician.", DateTime.UtcNow - start, last);
        }
    }

    /// <summary>Only watch the screen (target already booted into the test tool).</summary>
    public async Task<BootTestOutcome> WatchAsync(BootTestPlan plan, IBenchRig rig, Regex pass, Regex fail, DateTime start,
        IProgress<string>? progress, CancellationToken ct)
    {
        string? last = null;
        var deadline = start + TimeSpan.FromMinutes(plan.MaxMinutes);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(PollEvery, ct);
                last = await rig.ReadScreenAsync(ct) ?? last;
                if (last == null) continue;
                if (fail.IsMatch(last))
                {
                    await rig.ScreenshotAsync($"{plan.Name}: FAIL");
                    return new(BootTestResult.Fail, $"{plan.Name} FAILED.", DateTime.UtcNow - start, last);
                }
                if (pass.IsMatch(last))
                {
                    await rig.ScreenshotAsync($"{plan.Name}: PASS");
                    return new(BootTestResult.Pass, $"{plan.Name} passed.", DateTime.UtcNow - start, last);
                }
                progress?.Report($"Running... {(DateTime.UtcNow - start).TotalMinutes:0} min");
            }
            await rig.ScreenshotAsync($"{plan.Name}: timed out");
            return new(BootTestResult.Timeout, $"No result after {plan.MaxMinutes} min.", DateTime.UtcNow - start, last);
        }
        catch (OperationCanceledException)
        {
            return new(BootTestResult.Aborted, "Stopped by technician.", DateTime.UtcNow - start, last);
        }
    }
}
