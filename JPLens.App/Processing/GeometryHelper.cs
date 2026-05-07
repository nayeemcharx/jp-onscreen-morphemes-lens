using JPLens.Models;

namespace JPLens.Processing;

/// <summary>
/// Geometry utilities for physical-pixel rectangles.
/// All methods are pure functions with no side effects.
/// </summary>
public static class GeometryHelper
{
    /// <summary>
    /// Returns the smallest <see cref="PixelRect"/> that contains all
    /// rectangles in <paramref name="rects"/>.
    /// Returns <c>default</c> (Width/Height = 0) when the list is empty.
    /// </summary>
    public static PixelRect Union(IReadOnlyList<PixelRect> rects)
    {
        if (rects.Count == 0)
            return default;

        double minX = rects[0].X;
        double minY = rects[0].Y;
        double maxX = rects[0].Right;
        double maxY = rects[0].Bottom;

        for (int i = 1; i < rects.Count; i++)
        {
            var r = rects[i];
            if (r.X      < minX) minX = r.X;
            if (r.Y      < minY) minY = r.Y;
            if (r.Right  > maxX) maxX = r.Right;
            if (r.Bottom > maxY) maxY = r.Bottom;
        }

        return new PixelRect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// Returns the intersection over union (IoU) of two rectangles.
    /// Returns 0 when either rectangle has zero area.
    /// </summary>
    public static double IoU(PixelRect a, PixelRect b)
    {
        double interX1 = Math.Max(a.X,      b.X);
        double interY1 = Math.Max(a.Y,      b.Y);
        double interX2 = Math.Min(a.Right,  b.Right);
        double interY2 = Math.Min(a.Bottom, b.Bottom);

        if (interX2 <= interX1 || interY2 <= interY1)
            return 0.0;

        double interArea = (interX2 - interX1) * (interY2 - interY1);
        double unionArea  = a.Area + b.Area - interArea;

        return unionArea <= 0 ? 0.0 : interArea / unionArea;
    }

    /// <summary>Scales a rectangle by <paramref name="factor"/> around origin (0,0).</summary>
    public static PixelRect Scale(PixelRect rect, double factor)
        => new(rect.X * factor, rect.Y * factor, rect.Width * factor, rect.Height * factor);

    /// <summary>Translates a rectangle by (dx, dy).</summary>
    public static PixelRect Translate(PixelRect rect, double dx, double dy)
        => new(rect.X + dx, rect.Y + dy, rect.Width, rect.Height);

    /// <summary>
    /// Expands a rectangle by <paramref name="padding"/> on each side.
    /// The result may have negative width/height if padding is very negative.
    /// </summary>
    public static PixelRect AddPadding(PixelRect rect, double padding)
        => new(rect.X - padding, rect.Y - padding,
               rect.Width + padding * 2, rect.Height + padding * 2);
}
