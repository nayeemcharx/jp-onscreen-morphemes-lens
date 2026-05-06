using System.Drawing;
using System.Drawing.Imaging;
using JapaneseOcr.Interfaces;
using JapaneseOcr.Models;
using Microsoft.Extensions.Logging;

namespace JapaneseOcr.Services;

/// <summary>
/// Captures the primary monitor using the GDI BitBlt approach via
/// <see cref="Graphics.CopyFromScreen"/>.
///
/// LIMITATION — Exclusive fullscreen apps:
///   Some games and applications that use exclusive-mode Direct3D swap chains
///   may produce a black or stale image with BitBlt. In those cases the
///   DXGI Desktop Duplication API (IDXGIOutputDuplication) should be used
///   instead. The interface <see cref="IScreenCaptureService"/> allows swapping
///   this implementation without changing any other code.
///
/// DPI notes:
///   With PerMonitorV2 DPI awareness enabled via app.manifest, the process
///   receives physical pixel coordinates from all Win32 APIs. BitBlt
///   coordinates and dimensions are therefore in physical pixels.
/// </summary>
public sealed class WindowsScreenCaptureService : IScreenCaptureService
{
    private readonly ILogger<WindowsScreenCaptureService> _logger;

    public WindowsScreenCaptureService(ILogger<WindowsScreenCaptureService> logger)
        => _logger = logger;

    /// <inheritdoc/>
    public ScreenFrame CapturePrimaryMonitor()
    {
        var monitor = MonitorService.GetPrimaryMonitor();

        _logger.LogDebug(
            "Capturing monitor {W}×{H} at ({X},{Y}), scale={S:F2}",
            monitor.Width, monitor.Height, monitor.X, monitor.Y, monitor.ScaleFactor);

        // Allocate destination bitmap at native physical resolution
        var bitmap = new Bitmap(monitor.Width, monitor.Height, PixelFormat.Format32bppArgb);

        using var g = Graphics.FromImage(bitmap);

        // CopyFromScreen uses physical screen coordinates in a DPI-aware process
        g.CopyFromScreen(
            sourceX:      monitor.X,
            sourceY:      monitor.Y,
            destinationX: 0,
            destinationY: 0,
            blockRegionSize: new Size(monitor.Width, monitor.Height),
            copyPixelOperation: CopyPixelOperation.SourceCopy);

        return new ScreenFrame(
            image:        bitmap,
            width:        monitor.Width,
            height:       monitor.Height,
            monitorX:     monitor.X,
            monitorY:     monitor.Y,
            scaleFactor:  monitor.ScaleFactor,
            capturedAt:   DateTime.UtcNow);
    }
}
