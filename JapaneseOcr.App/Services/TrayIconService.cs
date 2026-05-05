using System.Drawing;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.Logging;
using WpfApplication = System.Windows.Application;
using GdiFontStyle   = System.Drawing.FontStyle;

namespace JapaneseOcr.Services;

/// <summary>
/// Manages the system-tray icon, context menu, and balloon notifications.
/// Call <see cref="Start"/> during application startup and
/// <see cref="Dispose"/> on shutdown.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly ILogger<TrayIconService> _logger;
    private TaskbarIcon?                      _trayIcon;

    /// <summary>Set by App.xaml.cs after DI container is built.</summary>
    public Action? OnRunOcr { get; set; }

    public TrayIconService(ILogger<TrayIconService> logger)
        => _logger = logger;

    public void Start()
    {
        _trayIcon = new TaskbarIcon
        {
            Icon        = CreateIcon(),
            ToolTipText = "Japanese OCR  (Ctrl+Shift+J)",
        };

        // Build context menu
        var menu = new System.Windows.Controls.ContextMenu();

        var runItem = new System.Windows.Controls.MenuItem { Header = "Run OCR (Ctrl+Shift+J)" };
        runItem.Click += (_, _) => OnRunOcr?.Invoke();

        var quitItem = new System.Windows.Controls.MenuItem { Header = "Quit" };
        quitItem.Click += (_, _) => WpfApplication.Current?.Shutdown();

        menu.Items.Add(runItem);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(quitItem);

        _trayIcon.ContextMenu = menu;

        _logger.LogInformation("Tray icon started");
    }

    /// <summary>Shows a transient balloon notification from the tray icon.</summary>
    public void ShowBalloon(string title, string message,
        BalloonIcon icon = BalloonIcon.Info)
    {
        _trayIcon?.ShowBalloonTip(title, message, icon);
    }

    public void Dispose()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Programmatically creates a simple 16×16 tray icon.</summary>
    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Blue circle background
            g.FillEllipse(Brushes.RoyalBlue, 0, 0, 15, 15);

            // White "J" label
            using var font = new Font("Arial", 8f, GdiFontStyle.Bold, GraphicsUnit.Point);
            g.DrawString("J", font, Brushes.White, 2f, 1f);
        }

        return Icon.FromHandle(bmp.GetHicon());
    }
}
