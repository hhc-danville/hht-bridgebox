using System.Text.Json;
using HHT.Bridge.Core.Monitoring;

namespace HHT.Bridge.Core.Settings;

public enum MouseMode { Absolute, Relative }

/// <summary>User settings, stored as JSON in the user's local app-data folder.</summary>
public sealed class ConsoleSettings
{
    public string? VideoDeviceName { get; set; }
    public string VideoMode { get; set; } = "1920x1080@30";
    public string? SerialPort { get; set; }
    public int BaudRate { get; set; } = 9600;
    public MouseMode MouseMode { get; set; } = MouseMode.Absolute;
    public bool HideLocalCursorWhenCaptured { get; set; } = true;
    public int TypingDelayMs { get; set; } = 10;
    public string? ScreenshotFolder { get; set; }

    // --- automation ---
    public string BiosBrand { get; set; } = "Generic / unknown";
    public bool AutoReadScreen { get; set; }

    // --- bench alerts ---
    public bool AlertsEnabled { get; set; }
    public double IdleMinutes { get; set; } = 10;
    public bool AlertIdle { get; set; } = true;
    public bool AlertChanged { get; set; }
    public bool AlertVideoLost { get; set; } = true;
    public string AlertText { get; set; } = "";
    public string? NotifyUrl { get; set; }

    // --- sharing / dashboard ---
    public string BenchId { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string BenchName { get; set; } = Environment.MachineName;
    public bool ShareEnabled { get; set; }
    public int SharePort { get; set; } = 8443;
    /// <summary>Shop-wide access key (same on every bench so the dashboard can show them all). Stored DPAPI-protected.</summary>
    public string? ShopKeyProtected { get; set; }

    // --- hardware ---
    public string? ControllerPort { get; set; }
    public string? VirtualMediaUrl { get; set; }
    public string? VirtualMediaTokenProtected { get; set; }
    public string? VirtualMediaFingerprint { get; set; }

    // --- v0.4 integrations ---
    public string RmmUrl { get; set; } = "https://rmm.hammerhillcomputers.com";
    public string? RmmKeyProtected { get; set; }
    public string AiUrl { get; set; } = "https://rmm.hammerhillcomputers.com/ai";
    public string? AiKeyProtected { get; set; }
    public string CloudUrl { get; set; } = "https://rmm.hammerhillcomputers.com/bridge";
    public string? CloudKeyProtected { get; set; }
    public bool CloudEnabled { get; set; }
    public string? LicenseKey { get; set; }
    public string? AgentUrl { get; set; }
    public string AgentArgs { get; set; } = "";
    public bool ReportToCustomer { get; set; }
    public int BillingIncrementMinutes { get; set; } = 15;

    public IEnumerable<AlertRule> BuildAlertRules()
    {
        if (AlertIdle) yield return new AlertRule(AlertKind.ScreenIdle, Math.Clamp(IdleMinutes, 0.5, 600));
        if (AlertChanged) yield return new AlertRule(AlertKind.ScreenChanged);
        if (AlertVideoLost) yield return new AlertRule(AlertKind.VideoLost);
        if (!string.IsNullOrWhiteSpace(AlertText)) yield return new AlertRule(AlertKind.TextAppears, Pattern: AlertText.Trim());
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static ConsoleSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<ConsoleSettings>(File.ReadAllText(path), Json) ?? new ConsoleSettings();
        }
        catch (Exception) { /* corrupt settings fall back to defaults */ }
        return new ConsoleSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Parses "WIDTHxHEIGHT@FPS".</summary>
    public static bool TryParseVideoMode(string mode, out int width, out int height, out int fps)
    {
        width = height = fps = 0;
        var at = mode.Split('@');
        var wh = at[0].Split('x', 'X');
        return wh.Length == 2 && at.Length == 2
            && int.TryParse(wh[0], out width) && int.TryParse(wh[1], out height) && int.TryParse(at[1], out fps)
            && width > 0 && height > 0 && fps > 0;
    }
}
