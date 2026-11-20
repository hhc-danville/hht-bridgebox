namespace HHT.Bridge.Core.Settings;

/// <summary>
/// Command-line options so other tools (e.g. the HHC-TechTool "Bridge Box" tab) can launch
/// the console pre-configured:
///   HHTBridgeConsole.exe --ticket 10423 --port COM5 --baud 9600 --tech "Steve" [--profile bench2]
/// --profile gives a separate settings/log folder so several consoles (benches) can run on one PC.
/// Unknown arguments are ignored.
/// </summary>
public sealed record LaunchOptions(string? Ticket, string? Port, int? Baud, string? Technician, string? Profile = null)
{
    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        string? ticket = null, port = null, tech = null, profile = null;
        int? baud = null;
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i].Trim();
            string? next = i + 1 < args.Count ? args[i + 1].Trim() : null;
            switch (a.ToLowerInvariant())
            {
                case "--ticket" when next != null: ticket = Clean(next); i++; break;
                case "--port" when next != null: port = next.ToUpperInvariant(); i++; break;
                case "--baud" when next != null:
                    if (int.TryParse(next, out var b) && b > 0) baud = b;
                    i++; break;
                case "--tech" when next != null: tech = next; i++; break;
                case "--profile" when next != null: profile = Clean(next); i++; break;
            }
        }
        return new LaunchOptions(ticket, port, baud, tech, profile);
    }

    /// <summary>Ticket IDs end up in folder names, so keep only safe characters.</summary>
    private static string? Clean(string s)
    {
        var chars = s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray();
        return chars.Length == 0 ? null : new string(chars);
    }
}
