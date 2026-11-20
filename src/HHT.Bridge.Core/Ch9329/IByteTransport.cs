namespace HHT.Bridge.Core.Ch9329;

/// <summary>
/// A byte pipe to the CH9329 (in production: a USB-serial COM port).
/// Read blocks for at most a short timeout and returns 0 when nothing arrived.
/// </summary>
public interface IByteTransport : IDisposable
{
    string Name { get; }
    bool IsOpen { get; }
    int Read(byte[] buffer, int offset, int count);
    void Write(byte[] buffer, int offset, int count);
}
