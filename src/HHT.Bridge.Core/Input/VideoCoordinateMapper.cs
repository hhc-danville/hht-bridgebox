namespace HHT.Bridge.Core.Input;

/// <summary>
/// Maps a pointer position over the on-screen video (drawn with uniform/letterbox scaling)
/// to the CH9329 absolute range 0..4095, and element-pixel deltas to target-pixel deltas.
/// </summary>
public static class VideoCoordinateMapper
{
    public readonly record struct Rect(double X, double Y, double Width, double Height);

    /// <summary>Where a frame of <paramref name="srcW"/>×<paramref name="srcH"/> is drawn inside the element.</summary>
    public static Rect DisplayedRect(double elementW, double elementH, int srcW, int srcH)
    {
        if (elementW <= 0 || elementH <= 0 || srcW <= 0 || srcH <= 0) return new Rect(0, 0, 0, 0);
        double scale = Math.Min(elementW / srcW, elementH / srcH);
        double w = srcW * scale, h = srcH * scale;
        return new Rect((elementW - w) / 2, (elementH - h) / 2, w, h);
    }

    /// <summary>
    /// Returns false when the point is in the letterbox bars (outside the picture).
    /// </summary>
    public static bool TryMapAbsolute(double px, double py, double elementW, double elementH, int srcW, int srcH,
        out int x, out int y, int max = Ch9329.Ch9329Protocol.AbsoluteMax)
    {
        x = y = 0;
        var r = DisplayedRect(elementW, elementH, srcW, srcH);
        if (r.Width <= 0 || r.Height <= 0) return false;
        double nx = (px - r.X) / r.Width;
        double ny = (py - r.Y) / r.Height;
        if (nx < 0 || nx > 1 || ny < 0 || ny > 1) return false;
        x = (int)Math.Round(nx * max);
        y = (int)Math.Round(ny * max);
        return true;
    }

    /// <summary>Scale for converting element-pixel movement to target-pixel movement.</summary>
    public static double ElementToSourceScale(double elementW, double elementH, int srcW, int srcH)
    {
        var r = DisplayedRect(elementW, elementH, srcW, srcH);
        return r.Width <= 0 ? 1.0 : srcW / r.Width;
    }
}
