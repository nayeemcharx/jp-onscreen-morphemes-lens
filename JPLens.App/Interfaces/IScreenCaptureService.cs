using JPLens.Models;

namespace JPLens.Interfaces;

/// <summary>
/// Captures a still frame from a monitor.
/// Implementations must return physical-pixel coordinates and honour the
/// current monitor's DPI scale factor.
/// </summary>
public interface IScreenCaptureService
{
    /// <summary>
    /// Captures the primary monitor and returns a <see cref="ScreenFrame"/>.
    /// The caller is responsible for disposing the returned frame.
    /// </summary>
    ScreenFrame CapturePrimaryMonitor();
}
