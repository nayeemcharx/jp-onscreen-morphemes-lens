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
using LinearGradientBrush = System.Windows.Media.LinearGradientBrush;
using GradientStop       = System.Windows.Media.GradientStop;
using GradientStopCollection = System.Windows.Media.GradientStopCollection;
using JPLens.Interfaces;
using JPLens.Models;
using JPLens.Services;
using FontFamily = System.Windows.Media.FontFamily;

namespace JPLens.Overlay;

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

    private readonly ILookupService               _lookup;
    private readonly AppSettings                    _settings;
    private IReadOnlyList<WordOverlay>              _overlays   = [];
    private WordOverlay?                            _hovered;
    private readonly HashSet<WordOverlay>           _selected   = [];

    // Monitor origin for coordinate translation (physical pixels)
    private int    _monitorX;
    private int    _monitorY;

    // DPI scale for this monitor (physical px / logical px)
    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;

    // Lookup popup — shown when a word box is clicked
    private readonly Border          _popupBorder;
    private readonly TextBlock       _popupReading;    // hiragana line
    private readonly TextBlock       _popupMeaning;    // English meaning line
    private readonly DispatcherTimer _popupTimer;      // auto-dismiss
    private CancellationTokenSource? _lookupCts;       // cancels in-flight lookups

    // Brush/pen caches for overlay drawing
    //
    // Glass-morphism style:
    //   Idle  — frosted blue-white, subtle cyan rim
    //   Hover — brighter ice-white fill, glowing white-cyan rim

    // Outer border strokes
    private static readonly Pen BoxStroke = CreateFrozenPen(
        Color.FromArgb(130, 180, 220, 255), thickness: 1.0);
    private static readonly Pen HovStroke = CreateFrozenPen(
        Color.FromArgb(220, 220, 245, 255), thickness: 1.5);

    // Inner glow ring (drawn 1 px inside the outer border)
    private static readonly Pen BoxInner = CreateFrozenPen(
        Color.FromArgb(50, 255, 255, 255), thickness: 1.0);
    private static readonly Pen HovInner = CreateFrozenPen(
        Color.FromArgb(90, 255, 255, 255), thickness: 1.0);

    // Selected state — amber/gold tint
    private static readonly Pen SelStroke = CreateFrozenPen(
        Color.FromArgb(230, 255, 200, 60), thickness: 1.5);
    private static readonly Pen SelInner = CreateFrozenPen(
        Color.FromArgb(80, 255, 240, 120), thickness: 1.0);

    // ──────────────────────────────────────────────────────────────────────────

    public OverlayWindow(ILookupService lookup, AppSettings settings)
    {
        _lookup   = lookup;
        _settings = settings;

        InitializeComponent();

        // ── Lookup popup ──────────────────────────────────────────────────────
        // Two-line panel: hiragana reading (large) + English meaning (small).
        // Positioned dynamically via Canvas.SetLeft/Top.
        _popupReading = new TextBlock
        {
            Foreground  = Brushes.White,
            FontSize    = 18,
            FontFamily  = new FontFamily("Meiryo, MS Gothic, Segoe UI"),
            FontWeight  = FontWeights.Bold,
        };

        _popupMeaning = new TextBlock
        {
            Foreground   = new SolidColorBrush(Color.FromArgb(220, 180, 220, 255)),
            FontSize     = 12,
            FontFamily   = new FontFamily("Segoe UI, Arial"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth     = 300,
            Margin       = new Thickness(0, 4, 0, 0),
        };

        var stack = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
        stack.Children.Add(_popupReading);
        stack.Children.Add(_popupMeaning);

        _popupBorder = new Border
        {
            Background   = new SolidColorBrush(Color.FromArgb(235, 20, 20, 30)),
            BorderBrush  = new SolidColorBrush(Color.FromArgb(180, 100, 160, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child        = stack,
            Visibility   = Visibility.Collapsed,
        };
        OverlayCanvas.Children.Add(_popupBorder);

        _popupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _popupTimer.Tick += (_, _) =>
        {
            _popupTimer.Stop();
            _popupBorder.Visibility = Visibility.Collapsed;
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
        _selected.Clear();

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
        // Cancel any in-flight lookup
        var cts = System.Threading.Interlocked.Exchange(ref _lookupCts, null);
        cts?.Cancel();
        cts?.Dispose();

        Visibility = Visibility.Hidden;
        _overlays  = [];
        _hovered   = null;
        _selected.Clear();
        _popupTimer.Stop();
        _popupBorder.Visibility = Visibility.Collapsed;
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
        const double rx = 7.0;
        const double ry = 7.0;

        foreach (var ov in _overlays)
        {
            var r           = ToCanvasRect(ov.ScreenBoundingBox);
            bool isHovered  = ReferenceEquals(ov, _hovered);
            bool isSelected = _selected.Contains(ov);

            // ── 1. Base fill — frosted-glass body ─────────────────────────────
            Color fillTop, fillBot;
            if (isSelected)
            {
                fillTop = Color.FromArgb(110, 255, 210,  50);
                fillBot = Color.FromArgb( 60, 200, 140,  20);
            }
            else if (isHovered)
            {
                fillTop = Color.FromArgb(100, 200, 225, 255);
                fillBot = Color.FromArgb( 70,  90, 150, 230);
            }
            else
            {
                fillTop = Color.FromArgb( 55, 160, 200, 245);
                fillBot = Color.FromArgb( 30,  60, 100, 200);
            }

            var bodyFill = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(fillTop, 0.0),
                    new GradientStop(fillBot, 1.0),
                },
                startPoint: new Point(0, 0), endPoint: new Point(0, 1));

            dc.DrawRoundedRectangle(bodyFill, null, r, rx, ry);

            // ── 2. Specular highlight — top ~48 % of box ────────────────────
            double specH = Math.Max(3.0, r.Height * 0.48);
            var specRect = new Rect(r.X + 2, r.Y + 1, Math.Max(0, r.Width - 4), specH);
            byte specAlpha1 = isSelected ? (byte)100 : isHovered ? (byte)130 : (byte)80;
            byte specAlpha2 = isSelected ? (byte) 25 : isHovered ? (byte) 30  : (byte)15;
            var specFill = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(specAlpha1, 255, 255, 255), 0.0),
                    new GradientStop(Color.FromArgb(specAlpha2, 255, 255, 255), 0.55),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0),
                },
                startPoint: new Point(0, 0), endPoint: new Point(0, 1));

            dc.DrawRoundedRectangle(specFill, null, specRect, rx - 1, ry - 1);

            // ── 3. Bottom rim light — faint glow along the lower edge ────────
            double rimH = Math.Max(2.0, r.Height * 0.22);
            var rimRect = new Rect(r.X + 3, r.Bottom - rimH - 1, Math.Max(0, r.Width - 6), rimH);
            Color rimColor = isSelected ? Color.FromArgb(50, 255, 200, 60) : Color.FromArgb(isHovered ? (byte)55 : (byte)28, 180, 220, 255);
            var rimFill = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0, rimColor.R, rimColor.G, rimColor.B), 0.0),
                    new GradientStop(rimColor, 1.0),
                },
                startPoint: new Point(0, 0), endPoint: new Point(0, 1));

            dc.DrawRoundedRectangle(rimFill, null, rimRect, rx - 2, ry - 2);

            // ── 4. Outer border + inner glow ring ──────────────────────────
            Pen outerPen = isSelected ? SelStroke : isHovered ? HovStroke : BoxStroke;
            Pen innerPen = isSelected ? SelInner  : isHovered ? HovInner  : BoxInner;
            dc.DrawRoundedRectangle(null, outerPen, r, rx, ry);

            var inner = new Rect(r.X + 1.5, r.Y + 1.5, r.Width - 3, r.Height - 3);
            dc.DrawRoundedRectangle(null, innerPen, inner, rx - 1, ry - 1);
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
        var pos = e.GetPosition(OverlayCanvas);
        var hit = FindOverlayAtCanvasPoint(pos.X, pos.Y);

        if (hit is not null && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            // Ctrl+Click — toggle selection
            if (!_selected.Remove(hit))
                _selected.Add(hit);

            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (hit is not null)
        {
            // Plain click — translate selected set (or just the clicked box)
            string textToLookup;
            WordOverlay representativeOverlay;

            if (_selected.Count > 0)
            {
                textToLookup        = BuildCombinedText(_selected);
                representativeOverlay = hit;
            }
            else
            {
                textToLookup        = hit.SurfaceText;
                representativeOverlay = hit;
            }

            var prev = System.Threading.Interlocked.Exchange(ref _lookupCts, new CancellationTokenSource());
            prev?.Cancel();
            prev?.Dispose();

            ShowLoadingPopupForText(textToLookup, representativeOverlay, pos);
            _ = DoLookupForTextAsync(textToLookup, representativeOverlay, pos, _lookupCts!.Token);
            e.Handled = true;
        }
        else
        {
            HideOverlay();
        }
    }

    /// <summary>
    /// Reconstructs the combined source text spanning all selected overlays.
    /// For overlays on the same line, extracts the substring from the earliest
    /// StartCharIndex to the latest EndCharIndex, which naturally includes any
    /// particles/prepositions between them.
    /// For overlays on different lines, concatenates their surface texts.
    /// </summary>
    private static string BuildCombinedText(IEnumerable<WordOverlay> overlays)
    {
        // Group by source line
        var byLine = overlays
            .GroupBy(o => o.SourceLineText)
            .OrderBy(g => g.Min(o => o.StartCharIndex))
            .ToList();

        var parts = new System.Text.StringBuilder();
        foreach (var group in byLine)
        {
            int start = group.Min(o => o.StartCharIndex);
            int end   = group.Max(o => o.EndCharIndex);
            string lineText = group.Key;

            if (start >= 0 && end > start && end <= lineText.Length)
                parts.Append(lineText, start, end - start);
            else
                parts.Append(string.Join(string.Empty, group.Select(o => o.SurfaceText)));
        }

        return parts.ToString();
    }

    // ── Lookup popup helpers ──────────────────────────────────────────────────

    private void ShowLoadingPopup(WordOverlay overlay, Point canvasPos)
        => ShowLoadingPopupForText(overlay.SurfaceText, overlay, canvasPos);

    private void ShowLoadingPopupForText(string text, WordOverlay overlay, Point canvasPos)
    {
        _popupReading.Text      = text;
        _popupMeaning.Text      = "…";
        _popupBorder.Visibility = Visibility.Visible;
        PositionPopup(canvasPos);

        _popupTimer.Stop();
        _popupTimer.Start();
    }

    private Task DoLookupAsync(WordOverlay overlay, Point canvasPos, CancellationToken ct)
        => DoLookupForTextAsync(overlay.SurfaceText, overlay, canvasPos, ct);

    private async Task DoLookupForTextAsync(string text, WordOverlay overlay, Point canvasPos, CancellationToken ct)
    {
        var result = await _lookup.LookupAsync(text, ct);

        if (ct.IsCancellationRequested)
            return;

        if (result is not null)
        {
            // For single-token lookups, show reading in brackets if available.
            bool isSingleToken = text == overlay.SurfaceText;
            bool hasDifferentReading = isSingleToken
                                       && !string.IsNullOrWhiteSpace(overlay.Reading)
                                       && overlay.Reading != overlay.SurfaceText;

            _popupReading.Text = hasDifferentReading
                ? $"{text}  [{overlay.Reading}]"
                : text;

            _popupMeaning.Text = result.Meaning;
        }
        else
        {
            _popupMeaning.Text = "(lookup failed — is the LLM server running?)";
        }

        _popupTimer.Stop();
        _popupTimer.Start();
    }

    private void PositionPopup(Point canvasPos)
    {
        // Force a layout pass so ActualWidth/Height are valid
        _popupBorder.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));

        double left = canvasPos.X;
        double top  = Math.Max(0, canvasPos.Y - _popupBorder.DesiredSize.Height - 8);

        // Clamp so the popup doesn't go off the right edge
        double maxLeft = ActualWidth - _popupBorder.DesiredSize.Width - 4;
        if (left > maxLeft && maxLeft > 0)
            left = maxLeft;

        Canvas.SetLeft(_popupBorder, left);
        Canvas.SetTop (_popupBorder, top);
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

    private static Brush CreateGradientBrush(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(top,    0.0),
                new GradientStop(bottom, 1.0),
            },
            startPoint: new Point(0, 0),
            endPoint:   new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush CreateFrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen CreateFrozenPen(Color c, double thickness)
    {
        var pen = new Pen(CreateFrozenBrush(c), thickness);
        pen.Freeze();
        return pen;
    }

    // Legacy helpers kept for any future callers
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
