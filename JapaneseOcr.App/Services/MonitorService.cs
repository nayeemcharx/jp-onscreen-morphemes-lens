using System.Runtime.InteropServices;
using System.Windows.Forms;
using JapaneseOcr.Models;

namespace JapaneseOcr.Services;

/// <summary>
/// Queries physical monitor bounds and DPI information via Win32 and WinForms.
/// Designed for multi-monitor awareness; currently only the primary monitor
/// path is exercised by the application.
/// </summary>
public sealed class MonitorInfo
{
    public int    X           { get; init; }
    public int    Y           { get; init; }
    public int    Width       { get; init; }
    public int    Height      { get; init; }
    public double ScaleFactor { get; init; }
    public bool   IsPrimary   { get; init; }
}

public static class MonitorService
{
    // SHCore.dll — GetDpiForMonitor (Windows 8.1+)
    [DllImport("shcore.dll", SetLastError = false)]
    private static extern int GetDpiForMonitor(
        IntPtr   hmonitor,
        int      dpiType,   // MDT_EFFECTIVE_DPI = 0
        out uint dpiX,
        out uint dpiY);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private const uint MONITOR_DEFAULTTOPRIMARY = 2;

    /// <summary>Returns information for the primary monitor.</summary>
    public static MonitorInfo GetPrimaryMonitor()
    {
        var screen = Screen.PrimaryScreen
            ?? throw new InvalidOperationException("No primary monitor found.");

        // Screen.Bounds in a DPI-aware process returns physical pixels
        var bounds = screen.Bounds;

        double scale = GetDpiScale(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);

        return new MonitorInfo
        {
            X           = bounds.X,
            Y           = bounds.Y,
            Width       = bounds.Width,
            Height      = bounds.Height,
            ScaleFactor = scale,
            IsPrimary   = true,
        };
    }

    /// <summary>Returns information for all connected monitors.</summary>
    public static IReadOnlyList<MonitorInfo> GetAllMonitors()
    {
        var result = new List<MonitorInfo>();
        var primary = Screen.PrimaryScreen;

        foreach (var screen in Screen.AllScreens)
        {
            var bounds = screen.Bounds;
            double scale = GetDpiScale(
                bounds.X + bounds.Width  / 2,
                bounds.Y + bounds.Height / 2);

            result.Add(new MonitorInfo
            {
                X           = bounds.X,
                Y           = bounds.Y,
                Width       = bounds.Width,
                Height      = bounds.Height,
                ScaleFactor = scale,
                IsPrimary   = screen.Primary,
            });
        }

        return result;
    }

    // ──────────────────────────────────────────────────────────────────────────

    private static double GetDpiScale(int x, int y)
    {
        var pt      = new POINT { X = x, Y = y };
        var hMon    = MonitorFromPoint(pt, MONITOR_DEFAULTTOPRIMARY);
        int hr      = GetDpiForMonitor(hMon, 0 /* MDT_EFFECTIVE_DPI */, out uint dpiX, out _);

        return hr == 0 ? dpiX / 96.0 : 1.0;
    }
}
