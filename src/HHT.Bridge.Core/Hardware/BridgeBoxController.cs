using System.Text;
using System.Threading.Channels;
using HHT.Bridge.Core.Ch9329;

namespace HHT.Bridge.Core.Hardware;

public enum UsbSide { Host, Target }

public sealed record ControllerInfo(string Model, string Version, IReadOnlyList<string> Features)
{
    public bool Has(string feature) => Features.Contains(feature, StringComparer.OrdinalIgnoreCase);
}

public sealed record ControllerStatus(bool PowerLed, bool HddLed, UsbSide Usb, bool Busy);

/// <summary>Sensor clip readings: probe temperatures (°C, null = not fitted) and fan speed.</summary>
public sealed record SensorReadings(double? Probe1C, double? Probe2C, int FanRpm)
{
    public override string ToString() =>
        $"Probe 1: {(Probe1C is { } a ? $"{a:0.0} °C" : "—")}   Probe 2: {(Probe2C is { } b ? $"{b:0.0} °C" : "—")}   Fan: {(FanRpm > 0 ? $"{FanRpm} RPM" : "stopped / not connected")}";
}

/// <summary>
/// Host side of the HHT Bridge Box controller (RP2040 firmware in firmware/rp2040-controller):
/// ATX power/reset buttons through opto-isolators, power/HDD LED sensing, and a switchable USB port.
///
/// Protocol: ASCII lines terminated by '\n' over USB CDC serial (115200 8N1).
///   HHT?            → HHT-BRIDGEBOX-CTRL &lt;version&gt; &lt;features...&gt;
///   PWR SHORT|LONG  → OK PWR SHORT|LONG   (LONG = 6 s hold = forced power-off)
///   RST             → OK RST
///   USB HOST|TARGET → OK USB HOST|TARGET
///   STAT?           → STAT PWRLED=0|1 HDDLED=0|1 USB=HOST|TARGET BUSY=0|1
///   errors          → ERR &lt;reason&gt;
/// </summary>
public sealed class BridgeBoxController : IDisposable
{
    public const int DefaultBaud = 115200;
    public const string Banner = "HHT-BRIDGEBOX-CTRL";

    private readonly IByteTransport _transport;
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    private readonly SemaphoreSlim _cmdLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Thread? _reader;

    public BridgeBoxController(IByteTransport transport) => _transport = transport;

    public string PortName => _transport.Name;
    public ControllerInfo? Info { get; private set; }
    public event Action<Exception>? Faulted;

    public void Start()
    {
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "BridgeBox controller reader" };
        _reader.Start();
    }

    private void ReadLoop()
    {
        var buf = new byte[256];
        var line = new StringBuilder();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = _transport.Read(buf, 0, buf.Length);
                for (int i = 0; i < n; i++)
                {
                    char c = (char)buf[i];
                    if (c == '\n')
                    {
                        var s = line.ToString().Trim();
                        line.Clear();
                        if (s.Length > 0) _lines.Writer.TryWrite(s);
                    }
                    else if (c != '\r' && c >= ' ' && c < 127 && line.Length < 256) line.Append(c);
                }
            }
        }
        catch (Exception ex) when (!_cts.IsCancellationRequested) { Faulted?.Invoke(ex); }
        catch { }
    }

    /// <summary>Sends one command and returns the first reply line that matches <paramref name="expectPrefix"/> or "ERR".</summary>
    public async Task<string?> CommandAsync(string command, string expectPrefix, TimeSpan timeout)
    {
        await _cmdLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            while (_lines.Reader.TryRead(out _)) { } // drop stale lines
            var bytes = Encoding.ASCII.GetBytes(command + "\n");
            _transport.Write(bytes, 0, bytes.Length);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(timeout);
            try
            {
                while (true)
                {
                    var line = await _lines.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
                    if (line.StartsWith(expectPrefix, StringComparison.Ordinal) || line.StartsWith("ERR", StringComparison.Ordinal)) return line;
                }
            }
            catch (OperationCanceledException) { return null; }
        }
        finally { _cmdLock.Release(); }
    }

    public async Task<ControllerInfo?> IdentifyAsync(TimeSpan? timeout = null)
    {
        var line = await CommandAsync("HHT?", Banner, timeout ?? TimeSpan.FromMilliseconds(600)).ConfigureAwait(false);
        Info = ParseBanner(line);
        return Info;
    }

    public static ControllerInfo? ParseBanner(string? line)
    {
        if (line == null || !line.StartsWith(Banner + " ", StringComparison.Ordinal)) return null;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 2 ? null : new ControllerInfo(parts[0], parts[1], parts.Skip(2).ToArray());
    }

    public async Task<(bool Ok, string Reply)> PressPowerAsync(bool longPress) =>
        Ok(await CommandAsync(longPress ? "PWR LONG" : "PWR SHORT", "OK PWR", TimeSpan.FromSeconds(1)).ConfigureAwait(false));

    public async Task<(bool Ok, string Reply)> ResetAsync() =>
        Ok(await CommandAsync("RST", "OK RST", TimeSpan.FromSeconds(1)).ConfigureAwait(false));

    public async Task<(bool Ok, string Reply)> SetUsbAsync(UsbSide side) =>
        Ok(await CommandAsync(side == UsbSide.Target ? "USB TARGET" : "USB HOST", "OK USB", TimeSpan.FromSeconds(1)).ConfigureAwait(false));

    public async Task<ControllerStatus?> GetStatusAsync() =>
        ParseStatus(await CommandAsync("STAT?", "STAT ", TimeSpan.FromMilliseconds(600)).ConfigureAwait(false));

    public async Task<SensorReadings?> GetSensorsAsync() =>
        ParseSensors(await CommandAsync("SENS?", "SENS ", TimeSpan.FromMilliseconds(600)).ConfigureAwait(false));

    public static SensorReadings? ParseSensors(string? line)
    {
        if (line == null || !line.StartsWith("SENS ", StringComparison.Ordinal)) return null;
        var kv = line[5..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1]);
        double? T(string k) => kv.TryGetValue(k, out var v) && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
        int rpm = kv.TryGetValue("FAN1", out var f) && int.TryParse(f, out var r) ? r : 0;
        return new SensorReadings(T("T1"), T("T2"), rpm);
    }

    public static ControllerStatus? ParseStatus(string? line)
    {
        if (line == null || !line.StartsWith("STAT ", StringComparison.Ordinal)) return null;
        var kv = line[5..].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1].ToUpperInvariant());
        bool B(string k) => kv.TryGetValue(k, out var v) && v == "1";
        var usb = kv.TryGetValue("USB", out var u) && u == "TARGET" ? UsbSide.Target : UsbSide.Host;
        return new ControllerStatus(B("PWRLED"), B("HDDLED"), usb, B("BUSY"));
    }

    private static (bool, string) Ok(string? reply) => (reply != null && reply.StartsWith("OK", StringComparison.Ordinal), reply ?? "no reply");

    /// <summary>Finds a controller among serial ports (sends "HHT?" at 115200).</summary>
    public static async Task<(string Port, ControllerInfo Info)?> FindAsync(IEnumerable<string> ports, Func<string, int, IByteTransport> open, Action<string>? log = null)
    {
        foreach (var port in ports)
        {
            try
            {
                using var c = new BridgeBoxController(open(port, DefaultBaud));
                c.Start();
                var info = await c.IdentifyAsync().ConfigureAwait(false);
                if (info != null) { log?.Invoke($"Bridge Box controller {info.Version} on {port}."); return (port, info); }
            }
            catch (Exception ex) { log?.Invoke($"{port}: {ex.Message}"); }
        }
        return null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _reader?.Join(500);
        _transport.Dispose();
        _cts.Dispose();
    }
}
