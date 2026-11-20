using System.Text.RegularExpressions;

namespace HHT.Bridge.Core.Automation;

/// <summary>
/// One step of a guided repair. The technician reads <see cref="Instruction"/>, optionally runs the linked
/// macro or key chord, then presses Next — or "Check screen", which uses OCR and <see cref="ScreenRegex"/>
/// to choose the next step automatically.
/// </summary>
public sealed record PlaybookStep(
    string Id, string Title, string Instruction,
    string? MacroId = null, string? KeyChord = null,
    string? ScreenRegex = null, string? IfMatch = null, string? IfNoMatch = null, string? Next = null);

public sealed record Playbook(string Id, string Name, string Symptom, IReadOnlyList<PlaybookStep> Steps);

public sealed class PlaybookSession
{
    private readonly List<string> _history = new();
    public Playbook Playbook { get; }
    public PlaybookStep Current { get; private set; }
    public bool IsComplete { get; private set; }
    public IReadOnlyList<string> History => _history;

    public PlaybookSession(Playbook playbook)
    {
        if (playbook.Steps.Count == 0) throw new ArgumentException("Playbook has no steps.");
        Playbook = playbook;
        Current = playbook.Steps[0];
        _history.Add(Current.Id);
    }

    /// <summary>Advance: explicit Next, else the following step in the list. "done" or the end completes.</summary>
    public void Advance() => GoTo(Current.Next ?? NextInList());

    /// <summary>Branch on what is on screen. Returns true if the step had a check.</summary>
    public bool EvaluateScreen(string screenText)
    {
        if (Current.ScreenRegex == null) return false;
        bool match = Regex.IsMatch(screenText ?? "", Current.ScreenRegex, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        GoTo((match ? Current.IfMatch : Current.IfNoMatch) ?? Current.Next ?? NextInList());
        return true;
    }

    public bool Back()
    {
        if (_history.Count < 2) return false;
        _history.RemoveAt(_history.Count - 1);
        Current = Playbook.Steps.First(s => s.Id == _history[^1]);
        IsComplete = false;
        return true;
    }

    private string NextInList()
    {
        int i = Playbook.Steps.ToList().FindIndex(s => s.Id == Current.Id);
        return i + 1 < Playbook.Steps.Count ? Playbook.Steps[i + 1].Id : "done";
    }

    private void GoTo(string id)
    {
        if (id == "done") { IsComplete = true; return; }
        Current = Playbook.Steps.FirstOrDefault(s => s.Id == id) ?? throw new InvalidOperationException($"Playbook '{Playbook.Id}' has no step '{id}'.");
        _history.Add(id);
    }

    /// <summary>Checks every branch target exists (used by tests and when loading custom playbooks).</summary>
    public static IEnumerable<string> Validate(Playbook p)
    {
        var ids = p.Steps.Select(s => s.Id).ToHashSet();
        if (ids.Count != p.Steps.Count) yield return $"{p.Id}: duplicate step ids";
        foreach (var s in p.Steps)
            foreach (var t in new[] { s.Next, s.IfMatch, s.IfNoMatch })
                if (t != null && t != "done" && !ids.Contains(t)) yield return $"{p.Id}/{s.Id}: unknown target '{t}'";
        foreach (var s in p.Steps.Where(s => s.MacroId != null))
            if (MacroLibrary.BuiltIn.All(m => m.Id != s.MacroId)) yield return $"{p.Id}/{s.Id}: unknown macro '{s.MacroId}'";
        foreach (var s in p.Steps.Where(s => s.KeyChord != null))
            if (!KeyChord.TryParse(s.KeyChord!, out _)) yield return $"{p.Id}/{s.Id}: bad key chord '{s.KeyChord}'";
    }
}

public static class PlaybookLibrary
{
    public static readonly IReadOnlyList<Playbook> BuiltIn = new[]
    {
        new Playbook("no-boot-device", "No bootable device / OS not found", "PC powers on but says no bootable device, OS not found or Boot Device Not Found (3F0).", new[]
        {
            new PlaybookStep("bios", "Open BIOS setup", "Use Keys → Enter BIOS setup for this brand, then power on the PC.",
                ScreenRegex: @"nvme|ssd|sata|m\.2|hdd|hard\s*disk|storage", IfMatch: "detected", IfNoMatch: "notdetected"),
            new PlaybookStep("detected", "Drive is listed — check boot mode", "The drive appears in BIOS. Check Boot mode is UEFI (not Legacy/CSM) for Windows 10/11 installs and that 'Windows Boot Manager' is first in boot order. Save and exit (usually F10).",
                ScreenRegex: @"windows\s*boot\s*manager", IfMatch: "bootorder", IfNoMatch: "nobootmgr"),
            new PlaybookStep("bootorder", "Fix boot order", "Move Windows Boot Manager to the top, save (F10), and watch it boot. If Windows starts, you're done; if it goes to Automatic Repair, open the 'Automatic Repair loop' playbook.", KeyChord: "F10", Next: "done"),
            new PlaybookStep("nobootmgr", "Boot entry missing", "No Windows Boot Manager entry: the EFI partition is missing or damaged. Boot Windows install media (Hardware tab → mount a Windows ISO), choose Repair → Command Prompt, then run: bcdboot C:\\Windows (check the Windows drive letter with 'WinRE: list disks/volumes').",
                MacroId: "winre-cmd-diskpart", Next: "done"),
            new PlaybookStep("notdetected", "Drive not detected", "The drive is not seen by the BIOS. Check SATA mode (AHCI vs RAID/Intel RST: RAID can hide NVMe drives), reseat the drive and cable, try another port. If still missing, the drive has likely failed: plan data recovery.",
                ScreenRegex: @"raid|rst|intel\s*rapid", IfMatch: "raid", IfNoMatch: "done"),
            new PlaybookStep("raid", "RAID/RST mode", "Storage is in RAID/RST mode. Windows was probably installed in AHCI (or vice versa). Change only if you know which mode Windows was installed in: switching the wrong way causes INACCESSIBLE_BOOT_DEVICE.", Next: "done"),
        }),
        new Playbook("bitlocker", "BitLocker recovery screen", "Blue screen asking for a 48-digit BitLocker recovery key.", new[]
        {
            new PlaybookStep("keyid", "Note the Key ID", "Read the screen (Tools → Read screen now). Write down the first 8 characters of the Recovery Key ID shown.",
                ScreenRegex: @"recovery\s*key\s*id", IfMatch: "find", IfNoMatch: "keyid2"),
            new PlaybookStep("keyid2", "Show the Key ID", "If the Key ID isn't visible, press Esc for more recovery options or the 'Skip this drive' link, which shows the Key ID.", KeyChord: "Esc", Next: "find"),
            new PlaybookStep("find", "Find the key", "Look up the key: customer's Microsoft account (aka.ms/myrecoverykey), Azure AD/Entra for work devices, your RMM asset record, or a printout. Match the Key ID.", Next: "enter"),
            new PlaybookStep("enter", "Type the key", "Paste the 48 digits into Keys → Type text (dashes are optional) and press Enter. Never store the key in notes.",
                ScreenRegex: @"recovery\s*key", IfMatch: "wrong", IfNoMatch: "done"),
            new PlaybookStep("wrong", "Key not accepted", "Still on the recovery screen: the key or Key ID doesn't match. Without the right key the data cannot be recovered; confirm with the customer before reinstalling.", Next: "done"),
        }),
        new Playbook("auto-repair-loop", "Automatic Repair loop", "'Preparing Automatic Repair' / 'Automatic Repair couldn't repair your PC' every boot.", new[]
        {
            new PlaybookStep("adv", "Open Advanced options", "On the 'Automatic Repair couldn't repair your PC' screen choose Advanced options → Troubleshoot → Advanced options.",
                ScreenRegex: @"advanced\s*options", IfMatch: "sfc", IfNoMatch: "adv"),
            new PlaybookStep("sfc", "Uninstall latest update", "If it started after an update: Advanced options → Uninstall Updates → latest quality update. Otherwise continue to Startup Repair / Command Prompt.", Next: "cmd"),
            new PlaybookStep("cmd", "Offline checks", "Open Command Prompt from Advanced options. Run the 'WinRE: list disks/volumes' macro to find the Windows letter, then: chkdsk X: /f and sfc /scannow /offbootdir=X:\\ /offwindir=X:\\Windows", MacroId: "winre-cmd-diskpart", Next: "safe"),
            new PlaybookStep("safe", "Try Safe Mode", "Advanced options → Startup Settings → Restart → press 4 (Safe Mode) or 5 (with networking). If it boots, remove recently installed drivers/software.", KeyChord: "4", Next: "done"),
        }),
        new Playbook("safe-mode-from-windows", "Get into Safe Mode (Windows still boots)", "Need Safe Mode to remove malware, a bad driver or AV.", new[]
        {
            new PlaybookStep("run", "Set Safe Mode and restart", "Runs the 'Safe Mode with Networking' macro (admin command prompt, bcdedit, restart).", MacroId: "safe-mode-net", Next: "wait"),
            new PlaybookStep("wait", "Confirm Safe Mode", "Wait for the login screen; 'Safe Mode' appears in the corners when it worked.",
                ScreenRegex: @"safe\s*mode", IfMatch: "work", IfNoMatch: "wait2"),
            new PlaybookStep("wait2", "Not in Safe Mode yet", "Give it another minute, then Check screen again. If Windows booted normally, re-run the macro.", Next: "wait"),
            new PlaybookStep("work", "Do the work, then turn Safe Mode off", "When finished, run 'Turn Safe Mode off' or the PC will keep booting into Safe Mode.", MacroId: "safe-mode-off", Next: "done"),
        }),
        new Playbook("cmos-reset", "BIOS settings scrambled / CMOS errors", "CMOS checksum bad, date reset to 2000s, or strange BIOS settings.", new[]
        {
            new PlaybookStep("before", "Save the 'before' state", "Tools → BIOS compare → Save BEFORE on each important BIOS page (Boot, Storage, Security).", Next: "defaults"),
            new PlaybookStep("defaults", "Load defaults", "In BIOS setup press the 'Load optimized defaults' key (often F9 or F5, sometimes in the Exit menu), then set date/time.", KeyChord: "F9", Next: "battery"),
            new PlaybookStep("battery", "Replace CMOS battery", "If the date keeps resetting after power-off, replace the CR2032 coin cell. Re-check boot mode (UEFI) and SATA mode match how Windows was installed.", Next: "after"),
            new PlaybookStep("after", "Compare after", "Tools → BIOS compare → Compare with BEFORE on the same pages; the differences go into the report.", Next: "done"),
        }),
    };

    public static Playbook? Find(string id) => BuiltIn.FirstOrDefault(p => p.Id == id);
}
