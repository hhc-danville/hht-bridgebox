using System.Runtime.InteropServices;

namespace HHT.Bridge.TechConsole.Capture;

/// <summary>
/// Records 16 kHz mono 16-bit audio from a Windows input device (laptop mic, USB mic, or the HDMI capture
/// dongle's audio) using the built-in winmm waveIn API — no extra dependencies.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    public const int SampleRate = 16000;
    private const int BufferMs = 100, BufferCount = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveInCaps { public ushort wMid, wPid; public uint vDriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname; public uint dwFormats; public ushort wChannels, wReserved1; }

    [DllImport("winmm.dll")] private static extern int waveInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern int waveInGetDevCapsW(IntPtr id, ref WaveInCaps caps, int size);
    [DllImport("winmm.dll")] private static extern int waveInOpen(out IntPtr h, int dev, ref WaveFormatEx fmt, IntPtr cb, IntPtr inst, int flags);
    [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr h);
    [DllImport("winmm.dll")] private static extern int waveInStop(IntPtr h);
    [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr h);
    [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr h);

    private const uint WhdrDone = 0x01;
    private IntPtr _h;
    private readonly List<IntPtr> _headers = new();
    private readonly List<IntPtr> _buffers = new();
    private readonly List<short> _samples = new();
    private Thread? _poll;
    private volatile bool _running;

    public static IReadOnlyList<string> Devices()
    {
        var list = new List<string>();
        int n = waveInGetNumDevs();
        for (int i = 0; i < n; i++)
        {
            var caps = new WaveInCaps();
            list.Add(waveInGetDevCapsW((IntPtr)i, ref caps, Marshal.SizeOf<WaveInCaps>()) == 0 ? caps.szPname : $"Input {i}");
        }
        return list;
    }

    public bool IsRecording => _running;
    public double Seconds { get { lock (_samples) return _samples.Count / (double)SampleRate; } }

    public void Start(int device)
    {
        if (_running) return;
        var fmt = new WaveFormatEx { wFormatTag = 1, nChannels = 1, nSamplesPerSec = SampleRate, wBitsPerSample = 16, nBlockAlign = 2, nAvgBytesPerSec = SampleRate * 2 };
        int r = waveInOpen(out _h, device, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
        if (r != 0) throw new InvalidOperationException($"Could not open the audio input (waveInOpen error {r}).");
        int bytes = SampleRate * 2 * BufferMs / 1000;
        int hdrSize = Marshal.SizeOf<WaveHdr>();
        for (int i = 0; i < BufferCount; i++)
        {
            var buf = Marshal.AllocHGlobal(bytes);
            var hdr = Marshal.AllocHGlobal(hdrSize);
            Marshal.StructureToPtr(new WaveHdr { lpData = buf, dwBufferLength = (uint)bytes }, hdr, false);
            waveInPrepareHeader(_h, hdr, hdrSize);
            waveInAddBuffer(_h, hdr, hdrSize);
            _buffers.Add(buf);
            _headers.Add(hdr);
        }
        lock (_samples) _samples.Clear();
        _running = true;
        waveInStart(_h);
        _poll = new Thread(Poll) { IsBackground = true, Name = "Audio capture" };
        _poll.Start();
    }

    private void Poll()
    {
        int hdrSize = Marshal.SizeOf<WaveHdr>();
        while (_running)
        {
            foreach (var hdr in _headers)
            {
                var h = Marshal.PtrToStructure<WaveHdr>(hdr);
                if ((h.dwFlags & WhdrDone) == 0) continue;
                int n = (int)h.dwBytesRecorded / 2;
                var tmp = new short[n];
                Marshal.Copy(h.lpData, tmp, 0, n);
                lock (_samples) { if (_samples.Count < SampleRate * 600) _samples.AddRange(tmp); } // cap 10 minutes
                if (!_running) break;
                waveInUnprepareHeader(_h, hdr, hdrSize);
                Marshal.StructureToPtr(new WaveHdr { lpData = h.lpData, dwBufferLength = h.dwBufferLength }, hdr, false);
                waveInPrepareHeader(_h, hdr, hdrSize);
                waveInAddBuffer(_h, hdr, hdrSize);
            }
            Thread.Sleep(20);
        }
    }

    /// <summary>Stops and returns everything recorded.</summary>
    public short[] Stop()
    {
        if (!_running) { lock (_samples) return _samples.ToArray(); }
        _running = false;
        _poll?.Join(500);
        waveInStop(_h);
        waveInReset(_h);
        int hdrSize = Marshal.SizeOf<WaveHdr>();
        foreach (var hdr in _headers) { waveInUnprepareHeader(_h, hdr, hdrSize); Marshal.FreeHGlobal(hdr); }
        foreach (var b in _buffers) Marshal.FreeHGlobal(b);
        _headers.Clear();
        _buffers.Clear();
        waveInClose(_h);
        _h = IntPtr.Zero;
        lock (_samples) return _samples.ToArray();
    }

    public void Dispose() { if (_running) Stop(); }
}
