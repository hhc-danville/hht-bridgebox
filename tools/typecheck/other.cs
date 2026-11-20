#nullable enable
#pragma warning disable CS0067, CS8618
namespace OpenCvSharp
{
    public enum VideoCaptureAPIs { ANY, DSHOW, MSMF }
    public enum VideoCaptureProperties { FrameWidth, FrameHeight, Fps, FourCC, BufferSize }
    public enum ColorConversionCodes { BGRA2BGR, GRAY2BGR, BGR2GRAY }
    public enum InterpolationFlags { Linear, Area }
    public enum ImwriteFlags { JpegQuality }
    public struct Size { public Size(int w, int h) { } }
    public struct Scalar { public double Val0; }
    public struct MatType { public static readonly MatType CV_8UC3; public static bool operator ==(MatType a, MatType b) => true; public static bool operator !=(MatType a, MatType b) => false; public override bool Equals(object? o) => true; public override int GetHashCode() => 0; }
    public struct FourCC { public static FourCC FromFourChars(char a, char b, char c, char d) => default; }
    public class InputArray { public static implicit operator InputArray(Mat m) => null!; }
    public class OutputArray { public static implicit operator OutputArray(Mat m) => null!; }
    public class InputOutputArray { }
    public class ImageEncodingParam { public ImageEncodingParam(ImwriteFlags f, int v) { } }
    public class Mat : IDisposable
    {
        public Mat() { } public Mat(int rows, int cols, MatType t) { }
        public int Rows { get; } public int Cols { get; } public IntPtr Data { get; }
        public bool Empty() => true; public MatType Type() => default; public int Channels() => 3; public bool IsContinuous() => true;
        public long Step() => 0; public Mat Clone() => this; public void CopyTo(Mat m) { } public void Dispose() { }
    }
    public static class Cv2
    {
        public static void CvtColor(InputArray s, OutputArray d, ColorConversionCodes c, int dstCn = 0) { }
        public static void Resize(InputArray s, OutputArray d, Size sz, double fx = 0, double fy = 0, InterpolationFlags i = InterpolationFlags.Linear) { }
        public static void Absdiff(InputArray a, InputArray b, OutputArray d) { }
        public static Scalar Mean(InputArray a, InputArray? mask = null) => default;
        public static bool ImEncode(string ext, InputArray img, out byte[] buf, params ImageEncodingParam[] prms) { buf = Array.Empty<byte>(); return true; }
    }
    public class VideoCapture : IDisposable
    {
        public VideoCapture(int index, VideoCaptureAPIs api = VideoCaptureAPIs.ANY) { }
        public bool IsOpened() => false; public bool Set(VideoCaptureProperties p, double v) => true; public double Get(VideoCaptureProperties p) => 0;
        public bool Read(Mat m) => false; public void Release() { } public void Dispose() { }
    }
    public class VideoWriter : IDisposable
    {
        public VideoWriter(string file, FourCC fourcc, double fps, Size size, bool isColor = true) { }
        public static int FourCC(char a, char b, char c, char d) => 0;
        public bool IsOpened() => false; public void Write(InputArray m) { } public void Release() { } public void Dispose() { }
    }
}
namespace Windows.Graphics.Imaging
{
    public enum BitmapPixelFormat { Bgra8 }
    public enum BitmapAlphaMode { Premultiplied }
    public sealed class SoftwareBitmap : IDisposable { public static SoftwareBitmap CreateCopyFromBuffer(Windows.Storage.Streams.IBuffer b, BitmapPixelFormat f, int w, int h, BitmapAlphaMode a) => null!; public void Dispose() { } }
}
namespace Windows.Storage.Streams { public interface IBuffer { } }
namespace Windows.Security.Cryptography { public static class CryptographicBuffer { public static Windows.Storage.Streams.IBuffer CreateFromByteArray(byte[] v) => null!; } }
namespace Windows.Globalization { public sealed class Language { public Language(string tag) { } public string LanguageTag => ""; } }
namespace Windows.Media.Ocr
{
    public sealed class OcrLine { public string Text => ""; }
    public sealed class OcrResult { public IReadOnlyList<OcrLine> Lines => Array.Empty<OcrLine>(); }
    public sealed class OcrEngine
    {
        public static OcrEngine? TryCreateFromUserProfileLanguages() => null;
        public static OcrEngine? TryCreateFromLanguage(Windows.Globalization.Language l) => null;
        public static uint MaxImageDimension => 2600;
        public Windows.Globalization.Language RecognizerLanguage => null!;
        public Task<OcrResult> RecognizeAsync(Windows.Graphics.Imaging.SoftwareBitmap b) => null!;
    }
}
namespace System.IO.Ports
{
    public enum Parity { None } public enum StopBits { One } public enum Handshake { None }
    public class SerialPort : IDisposable
    {
        public SerialPort(string port, int baud, Parity p, int bits, StopBits s) { }
        public Handshake Handshake { get; set; } public int ReadTimeout { get; set; } public int WriteTimeout { get; set; }
        public bool DtrEnable { get; set; } public bool RtsEnable { get; set; } public string PortName => ""; public bool IsOpen => false;
        public void Open() { } public void Close() { } public void DiscardInBuffer() { } public void Dispose() { }
        public int Read(byte[] b, int o, int c) => 0; public void Write(byte[] b, int o, int c) { }
        public static string[] GetPortNames() => Array.Empty<string>();
    }
}
namespace System.Management
{
    public class ManagementBaseObject : IDisposable { public object this[string n] => null!; public void Dispose() { } }
    public class ManagementObjectCollection : List<ManagementBaseObject>, IDisposable { public void Dispose() { } }
    public class ManagementObjectSearcher : IDisposable { public ManagementObjectSearcher(string q) { } public ManagementObjectCollection Get() => new(); public void Dispose() { } }
}
namespace System.Security.Cryptography
{
    public enum DataProtectionScope { CurrentUser, LocalMachine }
    public static class ProtectedData
    {
        public static byte[] Protect(byte[] d, byte[]? e, DataProtectionScope s) => d;
        public static byte[] Unprotect(byte[] d, byte[]? e, DataProtectionScope s) => d;
    }
}
namespace System.Media { public static class SystemSounds { public static SystemSound Exclamation => new(); } public class SystemSound { public void Play() { } } }
