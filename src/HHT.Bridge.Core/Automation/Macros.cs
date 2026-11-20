using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HHT.Bridge.Core.Ch9329;
using HHT.Bridge.Core.Hid;

namespace HHT.Bridge.Core.Automation;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MacroStepKind
{
    /// <summary>Press and release a chord, e.g. "Win+R".</summary>
    Keys,
    /// <summary>Type text (US layout). "\n" presses Enter.</summary>
    Text,
    /// <summary>Wait <see cref="MacroStep.Ms"/> milliseconds.</summary>
    Delay,
    /// <summary>Wait until on-screen text matches the regex in Value (needs OCR), up to Ms milliseconds.</summary>
    WaitForText,
}

public sealed record MacroStep(MacroStepKind Kind, string Value = "", int Ms = 0)
{
    public static MacroStep K(string chord) => new(MacroStepKind.Keys, chord);
    public static MacroStep T(string text) => new(MacroStepKind.Text, text);
    public static MacroStep Wait(int ms) => new(MacroStepKind.Delay, "", ms);
    public static MacroStep WaitText(string regex, int timeoutMs) => new(MacroStepKind.WaitForText, regex, timeoutMs);
}

public sealed record Macro(string Id, string Name, string Category, string Description, IReadOnlyList<MacroStep> Steps)
{
    /// <summary>Asks for confirmation before running (reboots, boot configuration changes).</summary>
    public bool Confirm { get; init; }
}

/// <summary>What a macro drives: the CH9329 keyboard plus (optionally) on-screen text recognition.</summary>
public interface IMacroTarget
{
    void SendKeyboard(KeyboardReport report);
    /// <summary>Returns true when text matching <paramref name="regex"/> is on screen before the timeout. Return false if OCR is unavailable.</summary>
    Task<bool> WaitForTextAsync(Regex regex, TimeSpan timeout, CancellationToken ct);
}

public sealed class MacroRunner
{
    /// <summary>Pause after each key press/release, so slow BIOS and UAC prompts keep up.</summary>
    public int KeyDelayMs { get; init; } = 12;
    /// <summary>Pause after each step.</summary>
    public int StepDelayMs { get; init; } = 150;

    public async Task<MacroResult> RunAsync(Macro macro, IMacroTarget target, CancellationToken ct = default, Action<string>? progress = null)
    {
        int stepNo = 0;
        try
        {
            foreach (var step in macro.Steps)
            {
                ct.ThrowIfCancellationRequested();
                stepNo++;
                switch (step.Kind)
                {
                    case MacroStepKind.Keys:
                        target.SendKeyboard(KeyChord.Parse(step.Value));
                        await Task.Delay(Math.Max(KeyDelayMs, 40), ct);
                        target.SendKeyboard(KeyboardReport.Empty);
                        break;
                    case MacroStepKind.Text:
                        foreach (var r in TextTyper.ToReports(step.Value).Reports)
                        {
                            target.SendKeyboard(r);
                            if (KeyDelayMs > 0) await Task.Delay(KeyDelayMs, ct);
                        }
                        break;
                    case MacroStepKind.Delay:
                        await Task.Delay(Math.Max(0, step.Ms), ct);
                        break;
                    case MacroStepKind.WaitForText:
                        progress?.Invoke($"Waiting for \"{step.Value}\" on screen...");
                        var ok = await target.WaitForTextAsync(new Regex(step.Value, RegexOptions.IgnoreCase), TimeSpan.FromMilliseconds(step.Ms <= 0 ? 30000 : step.Ms), ct);
                        if (!ok) return new MacroResult(false, stepNo, $"Timed out waiting for \"{step.Value}\".");
                        break;
                }
                if (StepDelayMs > 0) await Task.Delay(StepDelayMs, ct);
            }
            return new MacroResult(true, stepNo, "Completed.");
        }
        catch (OperationCanceledException)
        {
            target.SendKeyboard(KeyboardReport.Empty);
            return new MacroResult(false, stepNo, "Cancelled.");
        }
    }
}

public sealed record MacroResult(bool Success, int StepsRun, string Message);

/// <summary>Built-in technician macros plus user macros loaded from JSON.</summary>
public static class MacroLibrary
{
    private static MacroStep[] Run(string command) => new[]
    {
        MacroStep.K("Win+R"), MacroStep.Wait(900), MacroStep.T(command), MacroStep.K("Enter"),
    };

    /// <summary>Opens an elevated Command Prompt: Win+R, cmd, Ctrl+Shift+Enter, accept UAC with Alt+Y.</summary>
    private static IEnumerable<MacroStep> AdminCmd() => new[]
    {
        MacroStep.K("Win+R"), MacroStep.Wait(900), MacroStep.T("cmd"), MacroStep.K("Ctrl+Shift+Enter"),
        MacroStep.Wait(2500), MacroStep.K("Alt+Y"), MacroStep.Wait(2000),
    };

    private static Macro Admin(string id, string name, string desc, string command, bool confirm = false) =>
        new(id, name, "Admin command", desc, AdminCmd().Append(MacroStep.T(command + "\n")).ToArray()) { Confirm = confirm };

    public static readonly IReadOnlyList<Macro> BuiltIn = new List<Macro>
    {
        new("task-manager", "Task Manager", "Windows", "Ctrl+Shift+Esc.", new[] { MacroStep.K("Ctrl+Shift+Esc") }),
        new("run-devmgmt", "Device Manager", "Windows", "Win+R devmgmt.msc", Run("devmgmt.msc")),
        new("run-appwiz", "Programs and Features", "Windows", "Win+R appwiz.cpl", Run("appwiz.cpl")),
        new("run-msinfo", "System Information", "Windows", "Win+R msinfo32", Run("msinfo32")),
        new("run-eventvwr", "Event Viewer", "Windows", "Win+R eventvwr.msc", Run("eventvwr.msc")),
        new("run-diskmgmt", "Disk Management", "Windows", "Win+R diskmgmt.msc", Run("diskmgmt.msc")),
        new("run-services", "Services", "Windows", "Win+R services.msc", Run("services.msc")),
        new("run-ncpa", "Network Connections", "Windows", "Win+R ncpa.cpl", Run("ncpa.cpl")),
        new("admin-cmd", "Admin Command Prompt", "Admin command", "Opens an elevated Command Prompt (accepts UAC with Alt+Y).", AdminCmd().ToArray()),
        Admin("sfc", "SFC /scannow", "System file check.", "sfc /scannow"),
        Admin("dism", "DISM RestoreHealth", "Repair the component store.", "DISM /Online /Cleanup-Image /RestoreHealth"),
        Admin("chkdsk", "CHKDSK C: on next boot", "Schedules chkdsk /f /r for C: and answers Y.", "chkdsk C: /f /r\nY"),
        Admin("bitlocker-status", "BitLocker status", "manage-bde -status and key protector IDs.", "manage-bde -status & manage-bde -protectors -get C:"),
        Admin("ipconfig", "IP configuration", "ipconfig /all", "ipconfig /all"),
        Admin("reboot-firmware", "Reboot into BIOS/UEFI setup", "shutdown /r /fw: restarts straight into firmware setup on UEFI PCs.", "shutdown /r /fw /t 0", confirm: true),
        Admin("reboot-winre", "Reboot to Advanced Startup", "shutdown /r /o: restarts into the Windows Recovery menu.", "shutdown /r /o /t 0", confirm: true),
        Admin("safe-mode-on", "Safe Mode on next boot", "bcdedit safeboot minimal, then restart.", "bcdedit /set {current} safeboot minimal && shutdown /r /t 0", confirm: true),
        Admin("safe-mode-net", "Safe Mode with Networking", "bcdedit safeboot network, then restart.", "bcdedit /set {current} safeboot network && shutdown /r /t 0", confirm: true),
        Admin("safe-mode-off", "Turn Safe Mode off", "Removes the safeboot flag, then restarts.", "bcdedit /deletevalue {current} safeboot && shutdown /r /t 0", confirm: true),
        new("setup-cmd", "Windows Setup: command prompt", "Windows Setup", "Shift+F10 during Windows Setup / OOBE.", new[] { MacroStep.K("Shift+F10") }),
        new("oobe-local", "OOBE: local account", "Windows Setup",
            "Shift+F10 then 'start ms-cxh:localonly'. Microsoft has been closing these bypasses; may not work on the newest builds.",
            new[] { MacroStep.K("Shift+F10"), MacroStep.Wait(2000), MacroStep.T("start ms-cxh:localonly\n") }),
        new("win11-setup-assist", "Windows 11 Setup: to product key", "Windows Setup",
            "EXPERIMENTAL. Waits for each Setup screen (needs OCR) and presses Next: language, keyboard, setup option. Stops at the product key screen. Verify on your Windows media.",
            new[]
            {
                MacroStep.WaitText(@"language|time and currency|select\s*language", 300000), MacroStep.Wait(1500), MacroStep.K("Enter"),
                MacroStep.WaitText(@"keyboard|input\s*method", 60000), MacroStep.Wait(1500), MacroStep.K("Enter"),
                MacroStep.WaitText(@"setup\s*option|install\s*windows|install\s*now", 60000), MacroStep.Wait(1500), MacroStep.K("Enter"),
                MacroStep.WaitText(@"product\s*key", 120000),
            }) { Confirm = true },
        new("oobe-assist", "OOBE: region, keyboard, local account", "Windows Setup",
            "EXPERIMENTAL. On the first 'Is this the right country or region?' screen: accepts region and keyboard, skips the second layout, then opens Shift+F10 and starts the local-account flow.",
            new[]
            {
                MacroStep.WaitText(@"country\s*or\s*region", 600000), MacroStep.Wait(1500), MacroStep.K("Enter"),
                MacroStep.WaitText(@"keyboard\s*layout", 60000), MacroStep.Wait(1500), MacroStep.K("Enter"),
                MacroStep.WaitText(@"second\s*keyboard", 60000), MacroStep.Wait(1500), MacroStep.K("Tab"), MacroStep.K("Enter"),
                MacroStep.Wait(8000), MacroStep.K("Shift+F10"), MacroStep.Wait(2500), MacroStep.T("start ms-cxh:localonly\n"),
            }) { Confirm = true },
        new("winre-cmd-diskpart", "WinRE: list disks/volumes", "Recovery",
            "In a WinRE/Setup command prompt: diskpart list disk/vol (read-only).",
            new[] { MacroStep.T("diskpart\n"), MacroStep.Wait(2500), MacroStep.T("list disk\n"), MacroStep.Wait(800), MacroStep.T("list vol\n"), MacroStep.Wait(800), MacroStep.T("exit\n") }),
    };

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Builds the "hand off to agent" macro (item 8): once the customer PC is back in Windows, open an elevated
    /// PowerShell, download the shop's agent installer (TechDiag / RMM agent) over HTTPS and run it.
    /// Only plain https URLs and simple arguments are accepted so nothing unexpected is typed.
    /// </summary>
    public static Macro AgentInstall(string url, string arguments, string label = "agent")
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || !Regex.IsMatch(url, @"^[A-Za-z0-9:/?&=%._~+\-]+$"))
            throw new ArgumentException("Installer URL must be a plain https:// address.");
        if (!Regex.IsMatch(arguments ?? "", @"^[A-Za-z0-9 /:=._\-]*$"))
            throw new ArgumentException("Installer arguments may only contain letters, digits, spaces and / : = . _ -");
        var file = u.AbsolutePath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ? "hht-agent.msi" : "hht-agent.exe";
        var run = file.EndsWith(".msi")
            ? $"Start-Process msiexec.exe -ArgumentList '/i',$f,'/qn'{(string.IsNullOrWhiteSpace(arguments) ? "" : ",'" + arguments + "'")} -Wait"
            : $"Start-Process $f{(string.IsNullOrWhiteSpace(arguments) ? "" : " -ArgumentList '" + arguments + "'")} -Wait";
        var ps = $"$f=\"$env:TEMP\\{file}\"; Invoke-WebRequest -UseBasicParsing '{url}' -OutFile $f; {run}; Write-Host 'HHT agent install finished'";
        var steps = new[]
        {
            MacroStep.K("Win+R"), MacroStep.Wait(900), MacroStep.T("powershell"), MacroStep.K("Ctrl+Shift+Enter"),
            MacroStep.Wait(3000), MacroStep.K("Alt+Y"), MacroStep.Wait(2500), MacroStep.T(ps + "\n"),
        };
        return new Macro("agent-install", $"Install {label}", "Hand-off", $"Downloads {u.Host}{u.AbsolutePath} and runs it elevated.", steps) { Confirm = true };
    }

    /// <summary>Loads user macros (JSON array of <see cref="Macro"/>). Invalid files return an empty list and an error.</summary>
    public static IReadOnlyList<Macro> LoadUser(string path, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path)) return Array.Empty<Macro>();
            var list = JsonSerializer.Deserialize<List<Macro>>(File.ReadAllText(path), Json) ?? new List<Macro>();
            foreach (var m in list)
                foreach (var s in m.Steps.Where(s => s.Kind == MacroStepKind.Keys))
                    if (!KeyChord.TryParse(s.Value, out _)) throw new FormatException($"Macro '{m.Name}': unknown key chord '{s.Value}'.");
            return list;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return Array.Empty<Macro>();
        }
    }

    public static void SaveExample(string path)
    {
        if (File.Exists(path)) return;
        var example = new List<Macro>
        {
            new("my-example", "Example: open Notepad", "My macros", "Edit this file to add your own macros.",
                new[] { MacroStep.K("Win+R"), MacroStep.Wait(900), MacroStep.T("notepad\n") }),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(example, Json));
    }
}
