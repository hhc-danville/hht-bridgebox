namespace HHT.Bridge.Core.Ch9329;

/// <summary>
/// Finds which serial port / baud rate a CH9329 answers on by sending CMD_GET_INFO.
/// CH9329 modules ship at 9600 baud by default; many KVM cables are reconfigured to 115200.
/// </summary>
public static class Ch9329Probe
{
    public static readonly int[] CommonBaudRates = { 9600, 115200, 57600, 19200, 38400 };

    public sealed record Hit(string Port, int Baud, Ch9329Info Info);

    public static async Task<Hit?> FindAsync(
        IEnumerable<string> ports,
        Func<string, int, IByteTransport> open,
        IEnumerable<int>? baudRates = null,
        TimeSpan? perAttemptTimeout = null,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var timeout = perAttemptTimeout ?? TimeSpan.FromMilliseconds(400);
        foreach (var port in ports)
        {
            foreach (var baud in baudRates ?? CommonBaudRates)
            {
                ct.ThrowIfCancellationRequested();
                IByteTransport? t = null;
                try
                {
                    t = open(port, baud);
                    using var c = new Ch9329Controller(t);
                    t = null; // controller owns it now
                    c.Start();
                    var info = await c.GetInfoAsync(timeout).ConfigureAwait(false);
                    if (info != null)
                    {
                        log?.Invoke($"CH9329 found on {port} @ {baud} baud (chip {info.Version}).");
                        return new Hit(port, baud, info);
                    }
                    log?.Invoke($"No CH9329 reply on {port} @ {baud}.");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"Could not probe {port} @ {baud}: {ex.Message}");
                    t?.Dispose();
                    break; // port unusable (in use / missing) — skip other bauds
                }
            }
        }
        return null;
    }
}
