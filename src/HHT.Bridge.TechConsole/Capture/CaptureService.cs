using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace HHT.Bridge.TechConsole.Capture;

/// <summary>A BGR24 frame. The buffer is reused: consume it, then call <see cref="CaptureService.FrameConsumed"/>.</summary>
public sealed record VideoFrame(byte[] Buffer, int Width, int Height, int Stride);

/// <summary>
/// Pulls frames from a UVC capture device on a background thread (OpenCV, DirectShow backend).
/// Also provides: the latest frame for OCR/web sharing (JPEG, cached per frame), a "how much did
/// the picture change" metric for bench alerts, and MP4/AVI session recording.
/// If the UI is still drawing the previous frame, new frames are dropped rather than queued.
/// </summary>
public sealed class CaptureService : IDisposable
{
    private VideoCapture? _capture;
    private Thread? _thread;
    private volatile bool _running;
    private int _inFlight;
    private byte[] _buffer = Array.Empty<byte>();

    // latest frame (for OCR + web)
    private readonly object _latestGate = new();
    private Mat? _latest;
    private long _seq;
    private long _jpegSeq = -1;
    private byte[]? _jpeg;

    // change metric
    private Mat? _prevSmall;
    private int _diffCounter;

    // recording
    private readonly object _recGate = new();
    private VideoWriter? _writer;
    private string? _recPath;
    private string? _recPending;
    private Stopwatch? _recClock;
    private long _recFrames;
    private double _recFps = 30;

    public event Action<VideoFrame>? FrameReady;
    public event Action<string>? Problem;
    /// <summary>Mean grey-level difference between successive frames (0..255), ~6 times a second.</summary>
    public event Action<double>? ChangeMeasured;

    public bool IsRunning => _running;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public double RequestedFps { get; private set; }
    public double MeasuredFps { get; private set; }
    public long FrameSequence { get { lock (_latestGate) return _seq; } }
    public bool IsRecording { get { lock (_recGate) return _recPath != null || _recPending != null; } }
    public string? RecordingPath { get { lock (_recGate) return _recPath ?? _recPending; } }

    public bool Start(int deviceIndex, int width, int height, int fps, out string error)
    {
        Stop();
        var cap = new VideoCapture(deviceIndex, VideoCaptureAPIs.DSHOW);
        if (!cap.IsOpened())
        {
            cap.Dispose();
            error = "Could not open the capture device. It may be in use by another app (OBS, Camera, Teams), " +
                    "or Windows camera privacy settings may be blocking desktop apps.";
            return false;
        }

        // MJPG is required for 1080p30 on USB 2.0 dongles (MS2109); set it before the size.
        cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
        cap.Set(VideoCaptureProperties.FrameWidth, width);
        cap.Set(VideoCaptureProperties.FrameHeight, height);
        cap.Set(VideoCaptureProperties.Fps, fps);
        cap.Set(VideoCaptureProperties.BufferSize, 1);

        Width = (int)cap.Get(VideoCaptureProperties.FrameWidth);
        Height = (int)cap.Get(VideoCaptureProperties.FrameHeight);
        RequestedFps = cap.Get(VideoCaptureProperties.Fps);

        _capture = cap;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Video capture" };
        _thread.Start();
        error = "";
        return true;
    }

    public void FrameConsumed() => Interlocked.Exchange(ref _inFlight, 0);

    private void Loop()
    {
        using var mat = new Mat();
        int failures = 0, frames = 0;
        var clock = Stopwatch.StartNew();

        while (_running)
        {
            bool ok;
            try { ok = _capture!.Read(mat) && !mat.Empty(); }
            catch (Exception) { ok = false; }

            if (!ok)
            {
                if (++failures == 60) Problem?.Invoke("No frames from the capture device (unplugged, or in use by another app?).");
                Thread.Sleep(15);
                continue;
            }
            failures = 0;

            frames++;
            if (clock.ElapsedMilliseconds >= 1000)
            {
                MeasuredFps = frames * 1000.0 / clock.ElapsedMilliseconds;
                frames = 0;
                clock.Restart();
            }

            Mat src = mat;
            Mat? converted = null;
            if (mat.Type() != MatType.CV_8UC3)
            {
                converted = new Mat();
                Cv2.CvtColor(mat, converted, mat.Channels() == 4 ? ColorConversionCodes.BGRA2BGR : ColorConversionCodes.GRAY2BGR);
                src = converted;
            }
            if (!src.IsContinuous())
            {
                var clone = src.Clone();
                converted?.Dispose();
                converted = clone;
                src = clone;
            }
            Width = src.Cols;
            Height = src.Rows;

            try
            {
                KeepLatest(src);
                MeasureChange(src);
                Record(src);
            }
            catch (Exception ex) { Problem?.Invoke("Frame processing error: " + ex.Message); }

            if (Interlocked.CompareExchange(ref _inFlight, 1, 0) == 0)
            {
                int stride = (int)src.Step();
                int size = stride * src.Rows;
                if (_buffer.Length != size) _buffer = new byte[size];
                Marshal.Copy(src.Data, _buffer, 0, size);
                var handler = FrameReady;
                if (handler == null) FrameConsumed();
                else handler(new VideoFrame(_buffer, src.Cols, src.Rows, stride));
            }
            converted?.Dispose();
        }
        lock (_recGate) StopWriter_NoLock();
    }

    // ------------------------------------------------------------- latest frame / JPEG / OCR copy

    private void KeepLatest(Mat src)
    {
        lock (_latestGate)
        {
            if (_latest == null || _latest.Rows != src.Rows || _latest.Cols != src.Cols)
            {
                _latest?.Dispose();
                _latest = new Mat(src.Rows, src.Cols, MatType.CV_8UC3);
            }
            src.CopyTo(_latest);
            _seq++;
        }
    }

    /// <summary>Latest frame as JPEG (max 1280 px wide), encoded at most once per frame. Thread-safe.</summary>
    public (byte[]? Jpeg, long Sequence) GetJpeg(int quality = 70)
    {
        lock (_latestGate)
        {
            if (_latest == null || !_running) return (null, _seq);
            if (_jpegSeq != _seq || _jpeg == null)
            {
                Mat toEncode = _latest;
                Mat? scaled = null;
                if (_latest.Cols > 1280)
                {
                    scaled = new Mat();
                    Cv2.Resize(_latest, scaled, new Size(1280, (int)Math.Round(_latest.Rows * 1280.0 / _latest.Cols)), 0, 0, InterpolationFlags.Area);
                    toEncode = scaled;
                }
                Cv2.ImEncode(".jpg", toEncode, out var buf, new ImageEncodingParam(ImwriteFlags.JpegQuality, quality));
                scaled?.Dispose();
                _jpeg = buf;
                _jpegSeq = _seq;
            }
            return (_jpeg, _seq);
        }
    }

    /// <summary>Copy of the latest frame as tightly packed BGR24 (for OCR). Null when no video.</summary>
    public (byte[] Bgr, int Width, int Height)? CopyLatest()
    {
        lock (_latestGate)
        {
            if (_latest == null || !_running) return null;
            int stride = (int)_latest.Step();
            var data = new byte[stride * _latest.Rows];
            Marshal.Copy(_latest.Data, data, 0, data.Length);
            if (stride == _latest.Cols * 3) return (data, _latest.Cols, _latest.Rows);
            var packed = new byte[_latest.Cols * 3 * _latest.Rows];
            for (int y = 0; y < _latest.Rows; y++) Buffer.BlockCopy(data, y * stride, packed, y * _latest.Cols * 3, _latest.Cols * 3);
            return (packed, _latest.Cols, _latest.Rows);
        }
    }

    // ------------------------------------------------------------- change metric

    private void MeasureChange(Mat src)
    {
        if (++_diffCounter % 5 != 0) return; // ~6 per second at 30 fps
        using var small = new Mat();
        Cv2.Resize(src, small, new Size(64, 36), 0, 0, InterpolationFlags.Area);
        var gray = new Mat();
        Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);
        if (_prevSmall != null)
        {
            using var diff = new Mat();
            Cv2.Absdiff(gray, _prevSmall, diff);
            double mean = Cv2.Mean(diff).Val0;
            ChangeMeasured?.Invoke(mean);
            _prevSmall.Dispose();
        }
        _prevSmall = gray;
    }

    // ------------------------------------------------------------- recording

    /// <summary>Starts recording to <paramref name="pathWithoutExtension"/>.mp4 (falls back to .avi). The writer opens on the next frame.</summary>
    public void StartRecording(string pathWithoutExtension)
    {
        lock (_recGate)
        {
            StopWriter_NoLock();
            _recPending = pathWithoutExtension;
        }
    }

    /// <summary>Stops recording and returns the file path (null if nothing was recorded).</summary>
    public string? StopRecording()
    {
        lock (_recGate)
        {
            var path = _recPath;
            _recPending = null;
            StopWriter_NoLock();
            return path;
        }
    }

    private void Record(Mat src)
    {
        lock (_recGate)
        {
            if (_recPending != null)
            {
                _recFps = RequestedFps is > 1 and <= 60 ? RequestedFps : 30;
                var size = new Size(src.Cols, src.Rows);
                var mp4 = _recPending + ".mp4";
                var w = new VideoWriter(mp4, FourCC.FromFourChars('m', 'p', '4', 'v'), _recFps, size);
                string path = mp4;
                if (!w.IsOpened())
                {
                    w.Dispose();
                    path = _recPending + ".avi";
                    w = new VideoWriter(path, FourCC.FromFourChars('M', 'J', 'P', 'G'), _recFps, size);
                }
                _recPending = null;
                if (!w.IsOpened()) { w.Dispose(); Problem?.Invoke("Could not start recording (no video encoder available)."); return; }
                _writer = w;
                _recPath = path;
                _recClock = Stopwatch.StartNew();
                _recFrames = 0;
            }
            if (_writer == null) return;
            // Keep real-time pacing: write the current frame as many times as needed to match the clock.
            long due = (long)(_recClock!.Elapsed.TotalSeconds * _recFps) + 1;
            int writes = (int)Math.Clamp(due - _recFrames, 0, 5);
            for (int i = 0; i < writes; i++) { _writer.Write(src); _recFrames++; }
        }
    }

    private void StopWriter_NoLock()
    {
        _writer?.Release();
        _writer?.Dispose();
        _writer = null;
        _recPath = null;
        _recClock = null;
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(1500);
        _thread = null;
        lock (_recGate) StopWriter_NoLock();
        _capture?.Release();
        _capture?.Dispose();
        _capture = null;
        MeasuredFps = 0;
        _prevSmall?.Dispose();
        _prevSmall = null;
        FrameConsumed();
    }

    public void Dispose()
    {
        Stop();
        lock (_latestGate) { _latest?.Dispose(); _latest = null; }
    }
}
