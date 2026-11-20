using System.Text.RegularExpressions;

namespace HHT.Bridge.Core.Text;

public enum FindingKind
{
    Manufacturer,
    Model,
    SerialNumber,
    ServiceTag,
    BiosVersion,
    BiosDate,
    Processor,
    Memory,
    BootDevice,
    Warning,
    Error,
}

public sealed record Finding(FindingKind Kind, string Value, string SourceLine)
{
    public bool IsProblem => Kind is FindingKind.Warning or FindingKind.Error;
    public string Label => Kind switch
    {
        FindingKind.SerialNumber => "Serial number",
        FindingKind.ServiceTag => "Service tag",
        FindingKind.BiosVersion => "BIOS version",
        FindingKind.BiosDate => "BIOS date",
        FindingKind.BootDevice => "Boot device",
        _ => Kind.ToString(),
    };
}

/// <summary>
/// Pulls useful facts out of OCR text read from BIOS/UEFI, POST and boot-error screens:
/// identity (model, serial/service tag, BIOS version), hardware summary and known problem messages.
/// OCR text is noisy, so every pattern is tolerant and values are trimmed and length-capped.
/// </summary>
public static class BiosTextParser
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly (FindingKind Kind, Regex Rx)[] Fields =
    {
        (FindingKind.ServiceTag, new Regex(@"service\s*tag\s*[:#=]?\s*([A-Z0-9]{5,8})\b", Opt)),
        (FindingKind.SerialNumber, new Regex(@"(?:system\s+|product\s+|board\s+)?serial\s*(?:number|no\.?|#)?\s*[:#=]\s*([A-Z0-9][A-Z0-9\-]{3,24})", Opt)),
        (FindingKind.SerialNumber, new Regex(@"\bS/N\s*[:#=]?\s*([A-Z0-9][A-Z0-9\-]{3,24})", Opt)),
        (FindingKind.Model, new Regex(@"(?:product\s*name|system\s*model|model\s*name|model)\s*[:#=]\s*(.{3,60})", Opt)),
        (FindingKind.Manufacturer, new Regex(@"(?:system\s*)?manufacturer\s*[:#=]\s*(.{2,40})", Opt)),
        (FindingKind.BiosVersion, new Regex(@"bios\s*(?:version|revision|rev\.?|ver\.?)\s*[:#=]?\s*([A-Za-z0-9][A-Za-z0-9.\-_ ]{1,30})", Opt)),
        (FindingKind.BiosDate, new Regex(@"(?:bios\s*)?(?:build|release)\s*date\s*[:#=]?\s*(\d{1,4}[/\-.]\d{1,2}[/\-.]\d{1,4})", Opt)),
        (FindingKind.Processor, new Regex(@"(?:processor|cpu)\s*(?:type|name|model)?\s*[:#=]\s*(.{4,60})", Opt)),
        (FindingKind.Processor, new Regex(@"((?:Intel|AMD)\b.{0,10}(?:Core|Ryzen|Xeon|Pentium|Celeron|Athlon|EPYC)[^\r\n]{0,40})", Opt)),
        (FindingKind.Memory, new Regex(@"(?:total\s*memory|memory\s*(?:size|installed)|installed\s*memory|system\s*memory)\s*[:#=]?\s*(\d{1,6}\s*[MG]B)", Opt)),
        (FindingKind.BootDevice, new Regex(@"boot\s*(?:option|device)\s*#?\s*1\s*[:#=]?\s*(.{3,60})", Opt)),
    };

    private static readonly (FindingKind Kind, Regex Rx, string Message)[] Problems =
    {
        (FindingKind.Error, new Regex(@"no\s+bootable\s+(?:device|media)", Opt), "No bootable device: drive not detected, failed, or boot order is wrong."),
        (FindingKind.Error, new Regex(@"operating\s+system\s+not\s+found|missing\s+operating\s+system", Opt), "Operating system not found."),
        (FindingKind.Error, new Regex(@"boot\s*device\s*not\s*found", Opt), "Boot device not found (HP 3F0 style): check drive and boot mode."),
        (FindingKind.Error, new Regex(@"reboot\s+and\s+select\s+proper\s+boot\s+device", Opt), "Reboot and select proper boot device: no bootable drive."),
        (FindingKind.Error, new Regex(@"(?:s\.?m\.?a\.?r\.?t\.?|smart)\b[^\r\n]{0,40}(?:fail|bad|imminent|predict|error)", Opt), "SMART failure reported: back up data and replace the drive."),
        (FindingKind.Error, new Regex(@"hard\s*(?:disk|drive)[^\r\n]{0,30}(?:fail|error|not\s+(?:installed|detected))", Opt), "Hard drive failure / not detected."),
        (FindingKind.Error, new Regex(@"bitlocker\s+recovery", Opt), "BitLocker recovery screen: recovery key required."),
        (FindingKind.Error, new Regex(@"inaccessible_boot_device", Opt), "Stop code INACCESSIBLE_BOOT_DEVICE: storage driver/controller mode or disk problem."),
        (FindingKind.Error, new Regex(@"critical_process_died", Opt), "Stop code CRITICAL_PROCESS_DIED."),
        (FindingKind.Error, new Regex(@"stop\s*code\s*[:#]?\s*([A-Z_]{6,60})", Opt), "Windows stop code"),
        (FindingKind.Error, new Regex(@"automatic\s+repair\s+couldn.?t\s+repair", Opt), "Windows Automatic Repair failed."),
        (FindingKind.Warning, new Regex(@"preparing\s+automatic\s+repair", Opt), "Windows is in Automatic Repair."),
        (FindingKind.Warning, new Regex(@"cmos\s+(?:checksum|battery)[^\r\n]{0,20}(?:bad|low|error|fail)?", Opt), "CMOS checksum/battery problem: replace the CMOS battery (CR2032)."),
        (FindingKind.Warning, new Regex(@"(?:cpu|processor)\s*fan\s*(?:error|fail|speed)", Opt), "CPU fan error."),
        (FindingKind.Warning, new Regex(@"memory\s*(?:size|test)[^\r\n]{0,30}(?:fail|error|decreased|changed)", Opt), "Memory size changed / memory test failed: reseat or test RAM."),
        (FindingKind.Warning, new Regex(@"keyboard\s*(?:error|not\s+(?:detected|found))", Opt), "Keyboard error at POST."),
        (FindingKind.Warning, new Regex(@"thermal\s*(?:trip|shutdown|event)|over\s*temperature", Opt), "Thermal event: check cooling."),
        (FindingKind.Warning, new Regex(@"power\s+supply[^\r\n]{0,30}(?:fail|error|mismatch)|ac\s+adapter[^\r\n]{0,30}(?:not\s+recognized|unknown|wattage)", Opt), "Power supply / AC adapter warning."),
        (FindingKind.Warning, new Regex(@"secure\s+boot\s+violation", Opt), "Secure Boot violation."),
    };

    public static IReadOnlyList<Finding> Parse(string text)
    {
        var results = new List<Finding>();
        if (string.IsNullOrWhiteSpace(text)) return results;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

        foreach (var line in lines)
        {
            foreach (var (kind, rx) in Fields)
            {
                var m = rx.Match(line);
                if (!m.Success) continue;
                var value = Clean(m.Groups[1].Value);
                if (value.Length < 2) continue;
                if (seen.Add($"{kind}|{value}")) results.Add(new Finding(kind, value, line));
            }
            foreach (var (kind, rx, message) in Problems)
            {
                var m = rx.Match(line);
                if (!m.Success) continue;
                var value = message == "Windows stop code" ? $"Windows stop code {m.Groups[1].Value.ToUpperInvariant()}" : message;
                if (seen.Add($"{kind}|{value}")) results.Add(new Finding(kind, value, line));
            }
        }
        return results;
    }

    /// <summary>True when the screen looks like firmware setup or a boot menu (used to stop key-spamming).</summary>
    public static bool LooksLikeFirmwareScreen(string text) =>
        Regex.IsMatch(text ?? "",
            @"bios\s*setup|setup\s*utility|uefi|aptio|advanced\s*mode|ez\s*mode|boot\s*menu|boot\s*options?|please\s+select\s+boot\s+device|boot\s*manager|one[\s-]*time\s*boot|startup\s*menu|system\s*configuration|security\s*settings|main\s+advanced|f10\s*[:=]?\s*save",
            Opt);

    private static string Clean(string v)
    {
        v = Regex.Replace(v, @"\s{2,}.*$", ""); // OCR often runs two columns together; keep the first
        v = v.Trim(' ', ':', '.', '-', '|', '[', ']');
        return v.Length > 60 ? v[..60] : v;
    }
}
