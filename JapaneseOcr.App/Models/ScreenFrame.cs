using System.Drawing;

namespace JapaneseOcr.Models;

/// <summary>
/// A single captured frame from one monitor.
/// All dimensional values are in physical (device) pixels.
/// </summary>
public sealed class ScreenFrame : IDisposable
{
    /// <summary>The captured screen content.</summary>
    public Bitmap Image { get; }

    /// <summary>Width of the captured region in physical pixels.</summary>
    public int Width { get; }

    /// <summary>Height of the captured region in physical pixels.</summary>
    public int Height { get; }

    /// <summary>
    /// X offset of the monitor's top-left corner in the virtual screen coordinate space.
    /// For the primary monitor this is typically 0, but may be negative or positive
    /// for secondary monitors.
    /// </summary>
    public int MonitorX { get; }

    /// <summary>Y offset of the monitor's top-left corner in the virtual screen.</summary>
    public int MonitorY { get; }

    /// <summary>
    /// DPI scale factor for this monitor (e.g. 1.0 = 100%, 1.5 = 150%).
    /// Used to convert physical pixel coordinates to WPF logical units.
    /// </summary>
    public double ScaleFactor { get; }

    /// <summary>UTC time at which the screenshot was taken.</summary>
    public DateTime CapturedAt { get; }

    public ScreenFrame(
        Bitmap image,
        int width,
        int height,
        int monitorX,
        int monitorY,
        double scaleFactor,
        DateTime capturedAt)
    {
        Image       = image;
        Width       = width;
        Height      = height;
        MonitorX    = monitorX;
        MonitorY    = monitorY;
        ScaleFactor = scaleFactor;
        CapturedAt  = capturedAt;
    }

    public void Dispose() => Image.Dispose();
}
