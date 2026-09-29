namespace Glide.App;

// Caption regions use client-relative physical pixels, never screen positions.
internal readonly record struct CaptionBounds(int X, int Y, int Width, int Height)
{
    public static CaptionBounds FromDips(double x, double y, double width, double height,
        double clientWidth, double clientHeight, double scale)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height) ||
            !double.IsFinite(clientWidth) || !double.IsFinite(clientHeight) || !double.IsFinite(scale) ||
            width <= 0 || height <= 0 || clientWidth <= 0 || clientHeight <= 0 || scale <= 0) return default;
        // Round inward so the caption never steals a pixel from an adjacent button.
        double left = Math.Ceiling(Math.Clamp(x, 0, clientWidth) * scale);
        double top = Math.Ceiling(Math.Clamp(y, 0, clientHeight) * scale);
        double right = Math.Floor(Math.Clamp(x + width, 0, clientWidth) * scale);
        double bottom = Math.Floor(Math.Clamp(y + height, 0, clientHeight) * scale);
        if (right <= left || bottom <= top || right > int.MaxValue || bottom > int.MaxValue) return default;
        return new((int)left, (int)top, (int)(right - left), (int)(bottom - top));
    }
}
