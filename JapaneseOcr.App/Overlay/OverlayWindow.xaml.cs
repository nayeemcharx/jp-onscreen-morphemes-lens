using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Brush              = System.Windows.Media.Brush;
using Brushes            = System.Windows.Media.Brushes;
using Color              = System.Windows.Media.Color;
using DrawingContext      = System.Windows.Media.DrawingContext;
using Label              = System.Windows.Controls.Label;
using MouseEventArgs     = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Pen                = System.Windows.Media.Pen;
using Point              = System.Windows.Point;
using Rect               = System.Windows.Rect;
using Cursors             = System.Windows.Input.Cursors;
using SolidColorBrush    = System.Windows.Media.SolidColorBrush;
using JapaneseOcr.Interfaces;
using JapaneseOcr.Models;
using JapaneseOcr.Services;

namespace JapaneseOcr.Overlay;

/// <summary>
/// A borderless, transparent, always-on-top window that draws clickable overlay
/// boxes over detected Japanese words.
///
/// Coordinate system
/// ─────────────────
/// WordOverlay.ScreenBoundingBox is in absolute physical screen pixels.
/// WPF canvas coordinates are in logical pixels (DIPs at 96 DPI base).
/// The window is placed so that canvas (0,0) corresponds to the monitor's
/// physical top-left corner. All box positions are converted with:
///
///   canvasX = (physX - monitorX) / dpiScaleX
///   canvasY = (physY - monitorY) / dpiScaleY
///
/// Click-through behaviour
/// ───────────────────────
/// WM_NCHITTEST is intercepted in the HwndSource hook.  Points not over a
/// word box return HTTRANSPARENT, causing Windows to route the click to the
/// window beneath.  Points over a box return HTCLIENT so WPF mouse events fire.
///
/// Exclusive fullscreen limitation
/// ────────────────────────────────
/// Windows cannot place an overlay above a true exclusive-fullscreen Direct3D
/// swap chain.  The window will render above borderless-fullscreen and
/// windowed applications.  See README for details.
/// </summary>
public sealed partial class OverlayWindow : Window, IOverlayWindow
{
    // ── Win32 constants ───────────────────────────────────────────────────────
    private const int WM_NCHITTEST      = 0x0084;
    private const int HTTRANSPARENT     = -1;
    private const int HTCLIENT          = 1;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int GWL_EXSTYLE       = -20;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);

    // ──────────────────────────────────────────────────────────────────────────

    private readonly IClipboardService              _clipboard;
    private readonly AppSettings                    _settings;
    private IReadOnlyList<WordOverlay>              _overlays   = [];
    private WordOverlay?                            _hovered;

    // Monitor origin for coordinate translation (physical pixels)
    private int    _monitorX;
    private int    _monitorY;

    // DPI scale for this monitor (physical px / logical px)
    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;

    // Feedback popup shown after a word is copied
    private readonly Label       _feedbackLabel;
    private readonly DispatcherTimer _feedbackTimer;

    // Brush/pen caches for overlay drawing
    private static readonly Brush     BoxFill   = CreateBrush(255, 215, 0, 0.18);
    private static readonly Pen       BoxStroke = CreatePen(255, 215, 0, 0.80, thickness: 1.5);
    private static readonly Brush     HovFill   = CreateBrush(255, 215, 0, 0.38);
    private static readonly Pen       HovStroke = CreatePen(255, 255, 100, 0.95, thickness: 2.0);

    // ──────────────────────────────────────────────────────────────────────────

    public OverlayWindow(IClipboardService clipboard, AppSettings settings)
    {
        _clipboard = clipboard;
        _settings  = settings;

        InitializeComponent();

        // Feedback label — positioned dynamically via Canvas.SetLeft/Top
        _feedbackLabel = new Label
        {
            Background  = new SolidColorBrush(Color.FromArgb(220, 30, 30, 30)),
            Foreground  = Brushes.White,
            FontSize    = 13,
            Padding     = new Thickness(6, 2, 6, 2),
            Visibility  = Visibility.Collapsed,
        };
        OverlayCanvas.Children.Add(_feedbackLabel);

        _feedbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.5),
        };
        _feedbackTimer.Tick += (_, _) =>
        {
            _feedbackTimer.Stop();
            _feedbackLabel.Visibility = Visibility.Collapsed;
        };

        // Hook WM_NCHITTEST after the native window is created
        SourceInitialized += OnSourceInitialized;


    }

    // ── IOverlayWindow ────────────────────────────────────────────────────────

    public new bool IsVisible => Visibility == Visibility.Visible;

    /// <inheritdoc/>
    public void ShowOverlays(IReadOnlyList<WordOverlay> overlays)
    {
        _overlays = overlays;
        _hovered  = null;

        PositionWindowOnPrimaryMonitor();

        // Do NOT call Activate() or Focus() here.
        // The window is Topmost=true so it renders above everything without
        // needing to become the foreground window.  Calling Activate() invokes
        // SetForegroundWindow() which Windows restricts; when it fails silently
        // it leaves WPF and Win32 focus state out of sync, which causes the
        // hotkey Dispatcher.InvokeAsync callback to stop firing on subsequent
        // hotkey presses.  ESC is handled by the global Win32 hotkey and does
        // not require keyboard focus.
        Visibility = Visibility.Visible;

        // Invalidate the Window itself (not just the Canvas child) so that
        // Window.OnRender is re-called with the new _overlays list.
        // Calling OverlayCanvas.InvalidateVisual() only re-renders the canvas
        // element; it does NOT trigger Window.OnRender, so WPF keeps showing
        // the retained drawing instructions from the previous run.
        InvalidateVisual();
    }

    /// <inheritdoc/>
    public void HideOverlay()
    {
        Visibility = Visibility.Hidden;
        _overlays  = [];
        _hovered   = null;
        _feedbackTimer.Stop();
        _feedbackLabel.Visibility = Visibility.Collapsed;
    }

    // ── Custom rendering ──────────────────────────────────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        RenderOverlays(dc);
    }

    // OverlayCanvas does not draw itself; we render at the Window level so
    // overlays appear below the feedback label which is a Canvas child.
    private void RenderOverlays(DrawingContext dc)
    {
        const double cornerRadius = 4.0;

        foreach (var ov in _overlays)
        {
            var rect = ToCanvasRect(ov.ScreenBoundingBox);
            bool isHovered = ReferenceEquals(ov, _hovered);

            dc.DrawRoundedRectangle(
                brush:    isHovered ? HovFill   : BoxFill,
                pen:      isHovered ? HovStroke : BoxStroke,
                rectangle: rect,
                radiusX:  cornerRadius,
                radiusY:  cornerRadius);
        }
    }

    // ── Mouse event handlers (wired in XAML) ─────────────────────────────────

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        // Canvas logical coordinates
        var pos = e.GetPosition(OverlayCanvas);
        var hit = FindOverlayAtCanvasPoint(pos.X, pos.Y);

        if (!ReferenceEquals(hit, _hovered))
        {
            _hovered = hit;
            Mouse.OverrideCursor = hit is null ? null : Cursors.Hand;
            InvalidateVisual();
        }
    }

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pos      = e.GetPosition(OverlayCanvas);
        var selected = FindOverlayAtCanvasPoint(pos.X, pos.Y);

        if (selected is not null)
        {
            string copyText = selected.GetCopyText(_settings.CopyDictionaryForm);
            _clipboard.CopyText(copyText);
            ShowCopiedFeedback(selected, pos);
            e.Handled = true;
        }
        else
        {
            HideOverlay();
        }
    }

    // ── Feedback popup ────────────────────────────────────────────────────────

    private void ShowCopiedFeedback(WordOverlay overlay, Point canvasPos)
    {
        _feedbackLabel.Content    = $"Copied: {overlay.SurfaceText}";
        _feedbackLabel.Visibility = Visibility.Visible;

        // Position the label just above the click point
        Canvas.SetLeft(_feedbackLabel, canvasPos.X);
        Canvas.SetTop (_feedbackLabel, Math.Max(0, canvasPos.Y - 30));

        _feedbackTimer.Stop();
        _feedbackTimer.Start();
    }

    // ── Win32 click-through (WM_NCHITTEST hook) ───────────────────────────────

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        var source = HwndSource.FromHwnd(helper.Handle);
        source?.AddHook(WndProcHook);
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam,
                               ref bool handled)
    {
        if (msg == WM_NCHITTEST)
        {
            // Mouse position is in physical screen pixels (low word = X, high word = Y)
            // These are signed 16-bit values, sign-extended via short cast.
            short screenX = unchecked((short)(lParam.ToInt64() & 0xFFFF));
            short screenY = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));

            bool overBox = _overlays.Any(ov =>
                ov.ScreenBoundingBox.Contains(screenX, screenY));

            handled = true;
            return new IntPtr(overBox ? HTCLIENT : HTTRANSPARENT);
        }

        return IntPtr.Zero;
    }

    // ── Coordinate helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Converts a physical-pixel screen rect to WPF canvas logical coordinates.
    /// </summary>
    private Rect ToCanvasRect(PixelRect r)
        => new(
            (r.X - _monitorX) / _dpiScaleX,
            (r.Y - _monitorY) / _dpiScaleY,
            r.Width            / _dpiScaleX,
            r.Height           / _dpiScaleY);

    /// <summary>
    /// Finds the smallest-area overlay whose canvas rect contains the point.
    /// Returns null when no overlay matches.
    /// </summary>
    private WordOverlay? FindOverlayAtCanvasPoint(double cx, double cy)
    {
        // Convert back to physical screen pixels for Contains() check
        double screenX = cx * _dpiScaleX + _monitorX;
        double screenY = cy * _dpiScaleY + _monitorY;

        WordOverlay? best     = null;
        double       bestArea = double.MaxValue;

        foreach (var ov in _overlays)
        {
            if (ov.ScreenBoundingBox.Contains(screenX, screenY))
            {
                double area = ov.ScreenBoundingBox.Area;
                if (area < bestArea)
                {
                    best     = ov;
                    bestArea = area;
                }
            }
        }

        return best;
    }

    // ── Window positioning ────────────────────────────────────────────────────

    private void PositionWindowOnPrimaryMonitor()
    {
        var monitor = MonitorService.GetPrimaryMonitor();

        _monitorX   = monitor.X;
        _monitorY   = monitor.Y;
        _dpiScaleX  = monitor.ScaleFactor;
        _dpiScaleY  = monitor.ScaleFactor;

        // WPF position/size are in logical (DIP) pixels
        Left   = monitor.X / _dpiScaleX;
        Top    = monitor.Y / _dpiScaleY;
        Width  = monitor.Width  / _dpiScaleX;
        Height = monitor.Height / _dpiScaleY;
    }

    // ── Brush / Pen factories ─────────────────────────────────────────────────

    private static Brush CreateBrush(byte r, byte g, byte b, double opacity)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(byte r, byte g, byte b, double opacity, double thickness)
    {
        var pen = new Pen(CreateBrush(r, g, b, opacity), thickness);
        pen.Freeze();
        return pen;
    }
}
