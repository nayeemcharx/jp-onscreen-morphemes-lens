namespace JapaneseOcr.Models;

/// <summary>
/// An axis-aligned rectangle in physical screen pixel coordinates.
/// All coordinates are doubles to avoid rounding loss when scaling/translating.
/// "Physical pixels" means raw device pixels, independent of DPI scaling.
/// </summary>
public readonly struct PixelRect
{
    public double X      { get; init; }
    public double Y      { get; init; }
    public double Width  { get; init; }
    public double Height { get; init; }

    public double Right  => X + Width;
    public double Bottom => Y + Height;

    public PixelRect(double x, double y, double width, double height)
    {
        X      = x;
        Y      = y;
        Width  = width;
        Height = height;
    }

    /// <summary>
    /// Tests whether the given point (physical pixels) lies inside this rectangle.
    /// Boundaries are inclusive.
    /// </summary>
    public bool Contains(double px, double py)
        => px >= X && px <= Right && py >= Y && py <= Bottom;

    /// <summary>Area in square pixels.</summary>
    public double Area => Width * Height;

    /// <summary>Returns true when Width and Height are both positive.</summary>
    public bool IsValid => Width > 0 && Height > 0;

    public override string ToString()
        => $"PixelRect(X={X:F1}, Y={Y:F1}, W={Width:F1}, H={Height:F1})";
}
