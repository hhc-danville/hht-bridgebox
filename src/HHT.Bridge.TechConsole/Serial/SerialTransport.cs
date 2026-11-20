using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;
using HHT.Bridge.Core.Ch9329;

namespace HHT.Bridge.TechConsole.Serial;

/// <summary>COM-port transport to the CH9329 (8N1, no flow control).</summary>
public sealed class SerialTransport : IByteTransport
{
    private readonly SerialPort _port;

    public SerialTransport(string portName, int baud)
    {
        _port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = 200,
            WriteTimeout = 1000,
            DtrEnable = false,
            RtsEnable = false,
        };
        _port.Open();
        _port.DiscardInBuffer();
    }

    public string Name => _port.PortName;
    public bool IsOpen => _port.IsOpen;

    public int Read(byte[] buffer, int offset, int count)
    {
        try { return _port.Read(buffer, offset, count); }
        catch (TimeoutException) { return 0; }
    }

    public void Write(byte[] buffer, int offset, int count) => _port.Write(buffer, offset, count);

    public void Dispose()
    {
        try { if (_port.IsOpen) _port.Close(); } catch (IOException) { /* device already gone */ }
        _port.Dispose();
    }
}

public sealed record SerialPortEntry(string Port, string Description)
{
    /// <summary>CH9329 KVM cables use a CH340/CH341 or CP210x USB-UART on the laptop side.</summary>
    public bool LooksLikeKvmCable =>
        Description.Contains("CH340", StringComparison.OrdinalIgnoreCase)
        || Description.Contains("CH341", StringComparison.OrdinalIgnoreCase)
        || Description.Contains("CP210", StringComparison.OrdinalIgnoreCase)
        || Description.Contains("USB-SERIAL", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Description == Port ? Port : Description;
}

public static class SerialPortCatalog
{
    public static IReadOnlyList<SerialPortEntry> List()
    {
        var friendly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"] as string ?? "";
                var m = Regex.Match(name, @"\((COM\d+)\)");
                if (m.Success) friendly[m.Groups[1].Value] = name;
                obj.Dispose();
            }
        }
        catch (Exception) { /* WMI unavailable: fall back to bare names */ }

        return SerialPort.GetPortNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Length > 3 && int.TryParse(p.AsSpan(3), out var n) ? n : 999)
            .Select(p => new SerialPortEntry(p, friendly.TryGetValue(p, out var f) ? f : p))
            .ToList();
    }
}
