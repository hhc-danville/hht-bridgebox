using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace HHT.Bridge.TechConsole.Capture;

/// <summary>
/// Reads text from a video frame with the OCR engine built into Windows 10/11 (Windows.Media.Ocr).
/// Fully offline; uses the languages installed for the user (English is normally present).
/// </summary>
public sealed class OcrService
{
    private readonly OcrEngine? _engine;
    private readonly SemaphoreSlim _busy = new(1, 1);

    public OcrService()
    {
        try
        {
            _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                      ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
        }
        catch (Exception) { _engine = null; }
    }

    public bool Available => _engine != null;
    public string? LanguageTag => _engine?.RecognizerLanguage?.LanguageTag;

    /// <summary>Returns recognised text (one line per OCR line), or null if OCR is unavailable or already running.</summary>
    public async Task<string?> ReadAsync(byte[] bgr, int width, int height)
    {
        if (_engine == null) return null;
        if (!await _busy.WaitAsync(0)) return null; // skip if a read is already in progress
        try
        {
            // Scale down if the engine's limit is exceeded (nearest neighbour is fine for text at this size).
            int max = (int)OcrEngine.MaxImageDimension;
            int step = 1;
            while (width / step > max || height / step > max) step++;
            int w = width / step, h = height / step;

            var bgra = new byte[w * h * 4];
            await Task.Run(() =>
            {
                for (int y = 0; y < h; y++)
                {
                    int srcRow = (y * step) * width * 3;
                    int dstRow = y * w * 4;
                    for (int x = 0; x < w; x++)
                    {
                        int s = srcRow + (x * step) * 3;
                        int d = dstRow + x * 4;
                        bgra[d] = bgr[s];
                        bgra[d + 1] = bgr[s + 1];
                        bgra[d + 2] = bgr[s + 2];
                        bgra[d + 3] = 255;
                    }
                }
            });

            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                CryptographicBuffer.CreateFromByteArray(bgra), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
            var result = await _engine.RecognizeAsync(bitmap);
            return string.Join("\n", result.Lines.Select(l => l.Text));
        }
        finally
        {
            _busy.Release();
        }
    }
}
