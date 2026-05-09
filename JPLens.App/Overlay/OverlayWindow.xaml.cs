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
using System.Windows.Media.Animation;
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
    private readonly IClipboardService              _clipboard;
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

    public OverlayWindow(ILookupService lookup, AppSettings settings, IClipboardService clipboard)
    {
        _lookup    = lookup;
        _settings  = settings;
        _clipboard = clipboard;

        InitializeComponent();

        // ── Lookup popup ──────────────────────────────────────────────────────
        // Two-line panel: hiragana reading (large) + English meaning (small).
        // Positioned dynamically via Canvas.SetLeft/Top.
        _popupReading = new TextBlock
        {
            Foreground        = Brushes.White,
            FontSize          = 18,
            FontFamily        = new FontFamily("Meiryo, MS Gothic, Segoe UI"),
            FontWeight        = FontWeights.Bold,
            TextWrapping      = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _popupMeaning = new TextBlock
        {
            Foreground        = new SolidColorBrush(Color.FromArgb(220, 180, 220, 255)),
            FontSize          = 14,
            FontFamily        = new FontFamily("Segoe UI, Arial"),
            TextWrapping      = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // ── Close button (top-right title bar) ────────────────────────────────
        Border popupBorderRef = null!; // assigned just below; lambda safe to capture
        var closeBtn = MakeIconButton("\uE8BB", () => popupBorderRef.Visibility = Visibility.Collapsed);
        var titleRow = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        titleRow.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        titleRow.Children.Add(closeBtn);

        // ── Reading row: text + copy icon ─────────────────────────────────────
        var readingCopyBtn = MakeCopyButton(() => _clipboard.CopyText(_popupReading.Text));
        var readingRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(readingCopyBtn, Dock.Right);
        readingRow.Children.Add(readingCopyBtn);
        readingRow.Children.Add(_popupReading);

        // ── Meaning row: text + copy icon ─────────────────────────────────────
        var meaningCopyBtn = MakeCopyButton(() => _clipboard.CopyText(_popupMeaning.Text));
        var meaningRow = new DockPanel();
        DockPanel.SetDock(meaningCopyBtn, Dock.Right);
        meaningRow.Children.Add(meaningCopyBtn);
        meaningRow.Children.Add(_popupMeaning);

        var content = new StackPanel { Margin = new Thickness(10, 6, 6, 10) };
        content.Children.Add(titleRow);
        content.Children.Add(readingRow);
        content.Children.Add(meaningRow);

        _popupBorder = new Border
        {
            Background      = new SolidColorBrush(Color.FromArgb(235, 20, 20, 30)),
            BorderBrush     = new SolidColorBrush(Color.FromArgb(180, 100, 160, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(8),
            MaxWidth        = 500,
            Child           = content,
            Visibility      = Visibility.Collapsed,
        };
        popupBorderRef = _popupBorder;  // satisfy the close-button lambda
        OverlayCanvas.Children.Add(_popupBorder);

        // Timer is a no-op stub (popup is dismissed via the close button).
        _popupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };

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

        // ── Collect all rects that belong to the merged selection region ─────
        // For each line that has a selected token, include ALL overlays on that
        // line whose char range falls between the earliest and latest selected
        // token on that line — this fills the visual gap between selected boxes.
        var mergedRects = BuildMergedSelectionRects();

        // Draw non-selected overlays first (background layer)
        foreach (var ov in _overlays)
        {
            if (_selected.Contains(ov) || mergedRects.Contains(ov))
                continue;   // drawn as part of the merged selection region below

            var r          = ToCanvasRect(ov.ScreenBoundingBox);
            bool isHovered = ReferenceEquals(ov, _hovered);

            DrawSingleBox(dc, r, rx, ry, isHovered, isSelected: false);
        }

        // ── Draw merged selection region ─────────────────────────────────────
        if (mergedRects.Count > 0)
        {
            // Build a StreamGeometry containing one rounded-rect figure per box.
            // WPF fills the union automatically (NonZero fill rule).
            var geo = new System.Windows.Media.StreamGeometry();
            using (var ctx = geo.Open())
            {
                foreach (var ov in mergedRects)
                {
                    var r = ToCanvasRect(ov.ScreenBoundingBox);
                    // Approximate rounded rect via arc segments
                    AppendRoundedRectFigure(ctx, r, rx, ry);
                }
            }
            geo.Freeze();

            // Bounding rect of the whole selection for the gradient
            Rect bounds = default;
            foreach (var ov in mergedRects)
            {
                var r = ToCanvasRect(ov.ScreenBoundingBox);
                bounds = bounds == default ? r : Rect.Union(bounds, r);
            }

            var selFill = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(110, 255, 210,  50), 0.0),
                    new GradientStop(Color.FromArgb( 60, 200, 140,  20), 1.0),
                },
                startPoint: new Point(0, 0), endPoint: new Point(0, 1));

            dc.DrawGeometry(selFill, SelStroke, geo);

            // Inner glow ring — slightly inset geometry
            var geoInner = new System.Windows.Media.StreamGeometry();
            using (var ctx = geoInner.Open())
            {
                foreach (var ov in mergedRects)
                {
                    var r = ToCanvasRect(ov.ScreenBoundingBox);
                    var ri = new Rect(r.X + 1.5, r.Y + 1.5, r.Width - 3, r.Height - 3);
                    AppendRoundedRectFigure(ctx, ri, rx - 1, ry - 1);
                }
            }
            geoInner.Freeze();
            dc.DrawGeometry(null, SelInner, geoInner);
        }
    }

    // ── Line ordering helper ─────────────────────────────────────────────────

    private sealed record LineGroup(
        string              Key,
        TextOrientation     Orientation,
        List<WordOverlay>   Overlays);

    /// <summary>
    /// Returns all unique OCR lines from <paramref name="source"/> sorted in
    /// natural Japanese reading order:
    ///   Horizontal — top to bottom (ascending screen Y)
    ///   Vertical   — right to left (descending screen X)
    /// Mixed orientations: horizontal lines come before vertical ones.
    /// </summary>
    private static List<LineGroup> GetLinesInReadingOrder(IEnumerable<WordOverlay> source)
    {
        return source
            .GroupBy(o => o.SourceLineText)
            .Select(g =>
            {
                var orientation = g.First().LineOrientation;
                double repX = g.Average(o => o.ScreenBoundingBox.X);
                double repY = g.Average(o => o.ScreenBoundingBox.Y);
                return (Group: new LineGroup(g.Key, orientation, g.ToList()), RepX: repX, RepY: repY);
            })
            .OrderBy(t => t.Group.Orientation == TextOrientation.Vertical ? 1 : 0)
            .ThenBy(t => t.Group.Orientation  == TextOrientation.Horizontal ? t.RepY : -t.RepX)
            .Select(t => t.Group)
            .ToList();
    }

    /// <summary>
    /// Returns the set of overlays that should be drawn as part of the merged
    /// selection region.
    ///
    /// Rule: find the first and last selected lines in reading order.  For the
    /// first line include every overlay from the earliest selected token to the
    /// end of that line; for the last line from the start to the latest selected
    /// token; for any lines in between include all overlays on those lines.
    /// Within each boundary line, gaps between the selected tokens are also
    /// filled (so particles between two selected words are highlighted too).
    /// </summary>
    private HashSet<WordOverlay> BuildMergedSelectionRects()
    {
        if (_selected.Count == 0)
            return [];

        var allLines         = GetLinesInReadingOrder(_overlays);
        var selectedLineKeys = _selected.Select(o => o.SourceLineText).ToHashSet();

        // Indices of lines that contain at least one selected token
        var selectedIndices = allLines
            .Select((l, i) => (l, i))
            .Where(t => selectedLineKeys.Contains(t.l.Key))
            .Select(t => t.i)
            .ToList();

        if (selectedIndices.Count == 0)
            return new HashSet<WordOverlay>(_selected);

        int firstIdx = selectedIndices.Min();
        int lastIdx  = selectedIndices.Max();

        var result = new HashSet<WordOverlay>();

        for (int i = firstIdx; i <= lastIdx; i++)
        {
            var line     = allLines[i];
            bool isFirst  = i == firstIdx;
            bool isLast   = i == lastIdx;
            bool isSingle = isFirst && isLast;

            // Determine char-index span to include on this line
            int spanStart, spanEnd;

            if (isSingle)
            {
                // Single line: fill only between the selected tokens
                spanStart = _selected.Where(o => o.SourceLineText == line.Key).Min(o => o.StartCharIndex);
                spanEnd   = _selected.Where(o => o.SourceLineText == line.Key).Max(o => o.EndCharIndex);
            }
            else if (isFirst)
            {
                // First line: from the first selected token to the end of the line
                spanStart = _selected.Where(o => o.SourceLineText == line.Key).Min(o => o.StartCharIndex);
                spanEnd   = int.MaxValue;
            }
            else if (isLast)
            {
                // Last line: from the start of the line to the last selected token
                spanStart = 0;
                spanEnd   = _selected.Where(o => o.SourceLineText == line.Key).Max(o => o.EndCharIndex);
            }
            else
            {
                // Middle line: include everything
                spanStart = 0;
                spanEnd   = int.MaxValue;
            }

            foreach (var ov in line.Overlays)
                if (ov.StartCharIndex >= spanStart && ov.EndCharIndex <= spanEnd)
                    result.Add(ov);
        }

        return result;
    }

    private static void DrawSingleBox(DrawingContext dc, Rect r, double rx, double ry,
                                      bool isHovered, bool isSelected)
    {
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

        double specH = Math.Max(3.0, r.Height * 0.48);
        var specRect = new Rect(r.X + 2, r.Y + 1, Math.Max(0, r.Width - 4), specH);
        byte specAlpha1 = isHovered ? (byte)130 : (byte)80;
        byte specAlpha2 = isHovered ? (byte) 30 : (byte)15;
        var specFill = new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(specAlpha1, 255, 255, 255), 0.0),
                new GradientStop(Color.FromArgb(specAlpha2, 255, 255, 255), 0.55),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0),
            },
            startPoint: new Point(0, 0), endPoint: new Point(0, 1));

        dc.DrawRoundedRectangle(specFill, null, specRect, rx - 1, ry - 1);

        double rimH = Math.Max(2.0, r.Height * 0.22);
        var rimRect = new Rect(r.X + 3, r.Bottom - rimH - 1, Math.Max(0, r.Width - 6), rimH);
        Color rimColor = Color.FromArgb(isHovered ? (byte)55 : (byte)28, 180, 220, 255);
        var rimFill = new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0, rimColor.R, rimColor.G, rimColor.B), 0.0),
                new GradientStop(rimColor, 1.0),
            },
            startPoint: new Point(0, 0), endPoint: new Point(0, 1));

        dc.DrawRoundedRectangle(rimFill, null, rimRect, rx - 2, ry - 2);

        Pen outerPen = isHovered ? HovStroke : BoxStroke;
        Pen innerPen = isHovered ? HovInner  : BoxInner;
        dc.DrawRoundedRectangle(null, outerPen, r, rx, ry);

        var inner = new Rect(r.X + 1.5, r.Y + 1.5, r.Width - 3, r.Height - 3);
        dc.DrawRoundedRectangle(null, innerPen, inner, rx - 1, ry - 1);
    }

    /// <summary>
    /// Appends a rounded-rectangle figure to an open StreamGeometryContext.
    /// </summary>
    private static void AppendRoundedRectFigure(
        System.Windows.Media.StreamGeometryContext ctx,
        Rect r, double rx, double ry)
    {
        // Clamp radii
        rx = Math.Min(rx, r.Width  / 2);
        ry = Math.Min(ry, r.Height / 2);

        bool isStroked = true;
        bool isFilled  = true;
        var arcSize    = new System.Windows.Size(rx, ry);

        ctx.BeginFigure(new Point(r.Left + rx, r.Top), isFilled, isClosed: true);
        ctx.LineTo(new Point(r.Right - rx, r.Top),    isStroked, isSmoothJoin: false);
        ctx.ArcTo (new Point(r.Right, r.Top + ry),    arcSize, 0, false,
                   System.Windows.Media.SweepDirection.Clockwise, isStroked, isSmoothJoin: false);
        ctx.LineTo(new Point(r.Right, r.Bottom - ry), isStroked, isSmoothJoin: false);
        ctx.ArcTo (new Point(r.Right - rx, r.Bottom), arcSize, 0, false,
                   System.Windows.Media.SweepDirection.Clockwise, isStroked, isSmoothJoin: false);
        ctx.LineTo(new Point(r.Left + rx, r.Bottom),  isStroked, isSmoothJoin: false);
        ctx.ArcTo (new Point(r.Left, r.Bottom - ry),  arcSize, 0, false,
                   System.Windows.Media.SweepDirection.Clockwise, isStroked, isSmoothJoin: false);
        ctx.LineTo(new Point(r.Left, r.Top + ry),     isStroked, isSmoothJoin: false);
        ctx.ArcTo (new Point(r.Left + rx, r.Top),     arcSize, 0, false,
                   System.Windows.Media.SweepDirection.Clockwise, isStroked, isSmoothJoin: false);
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
            var merged = _selected.Count > 0 ? BuildMergedSelectionRects() : [];

            if (_selected.Count > 0 && merged.Contains(hit))
            {
                var lineRank = GetLinesInReadingOrder(_overlays)
                    .Select((l, i) => (l.Key, i))
                    .ToDictionary(t => t.Key, t => t.i);

                int hitLine = lineRank.GetValueOrDefault(hit.SourceLineText, 0);

                // Check if hit is the anchor (no explicitly-selected word comes before it)
                bool isAnchor = !_selected.Any(ov =>
                {
                    int ovLine = lineRank.GetValueOrDefault(ov.SourceLineText, 0);
                    if (ovLine != hitLine) return ovLine < hitLine;
                    return ov.StartCharIndex < hit.StartCharIndex;
                });

                if (isAnchor)
                {
                    // Clicking the anchor clears the whole selection
                    _selected.Clear();
                }
                else
                {
                    // Trim: keep only [anchor..hit], drop everything after hit
                    _selected.RemoveWhere(ov =>
                    {
                        int ovLine = lineRank.GetValueOrDefault(ov.SourceLineText, 0);
                        if (ovLine != hitLine) return ovLine > hitLine;
                        return ov.StartCharIndex > hit.StartCharIndex;
                    });
                    _selected.Add(hit);
                }
            }
            else
            {
                // Outside the current region — normal toggle
                if (!_selected.Remove(hit))
                    _selected.Add(hit);
            }

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
                // Use the merged set (includes gap-filled tokens) for text reconstruction
                var merged = BuildMergedSelectionRects();
                textToLookup          = BuildCombinedText(merged);
                representativeOverlay = hit;
            }
            else
            {
                textToLookup          = hit.SurfaceText;
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
            // Only hide if the click was not inside the visible popup
            if (_popupBorder.Visibility != Visibility.Visible ||
                !IsOverCanvasElement(_popupBorder, pos))
            {
                HideOverlay();
            }
        }
    }

    /// <summary>
    /// Reconstructs the combined source text from the merged overlay set.
    /// Lines are emitted in reading order.  For each line the substring of
    /// <see cref="WordOverlay.SourceLineText"/> from the earliest to the latest
    /// char index present in <paramref name="merged"/> is used, so particles
    /// and other filtered tokens that sit between two visible tokens are
    /// naturally included.
    /// </summary>
    private string BuildCombinedText(HashSet<WordOverlay> merged)
    {
        if (merged.Count == 0)
            return string.Empty;

        // Use the global line order so lines appear in the correct reading sequence
        var allLines         = GetLinesInReadingOrder(_overlays);
        var mergedLineKeys   = merged.Select(o => o.SourceLineText).ToHashSet();

        var parts = new System.Text.StringBuilder();

        foreach (var line in allLines)
        {
            if (!mergedLineKeys.Contains(line.Key))
                continue;

            int start    = merged.Where(o => o.SourceLineText == line.Key).Min(o => o.StartCharIndex);
            int end      = merged.Where(o => o.SourceLineText == line.Key).Max(o => o.EndCharIndex);
            string lineText = line.Key;

            if (start >= 0 && end > start && end <= lineText.Length)
                parts.Append(lineText, start, end - start);
            else
                parts.Append(string.Join(string.Empty,
                    merged.Where(o => o.SourceLineText == line.Key)
                          .OrderBy(o => o.StartCharIndex)
                          .Select(o => o.SurfaceText)));
        }

        return parts.ToString();
    }

    // ── Lookup popup helpers ──────────────────────────────────────────────────

    private void ShowLoadingPopup(WordOverlay overlay, Point canvasPos)
        => ShowLoadingPopupForText(overlay.SurfaceText, overlay, canvasPos);

    private void ShowLoadingPopupForText(string text, WordOverlay overlay, Point canvasPos)
    {
        bool isSingleToken   = text == overlay.SurfaceText;
        bool hasReading      = isSingleToken
                               && !string.IsNullOrWhiteSpace(overlay.Reading)
                               && overlay.Reading != overlay.SurfaceText;
        _popupReading.Text      = hasReading ? $"{text}  [{overlay.Reading}]" : text;
        _popupMeaning.Text      = "…";
        _popupBorder.Visibility = Visibility.Visible;
        PositionPopup(canvasPos);
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
            _popupMeaning.Text = result.Meaning;
        }
        else
        {
            _popupMeaning.Text = "(lookup failed — is the LLM server running?)";
        }
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
                ov.ScreenBoundingBox.Contains(screenX, screenY))
                || IsOverPopup(screenX, screenY);

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

    // ── Popup hit-test helper ─────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the physical-pixel point is inside the visible popup.
    /// Used by WM_NCHITTEST so WPF mouse events reach the popup's buttons.
    /// </summary>
    private bool IsOverPopup(double screenX, double screenY)
    {
        if (_popupBorder.Visibility != Visibility.Visible)
            return false;
        double left   = Canvas.GetLeft(_popupBorder);
        double top    = Canvas.GetTop(_popupBorder);
        double sx1 = left                              * _dpiScaleX + _monitorX;
        double sy1 = top                               * _dpiScaleY + _monitorY;
        double sx2 = (left + _popupBorder.ActualWidth)  * _dpiScaleX + _monitorX;
        double sy2 = (top  + _popupBorder.ActualHeight) * _dpiScaleY + _monitorY;
        return screenX >= sx1 && screenX <= sx2 && screenY >= sy1 && screenY <= sy2;
    }

    /// <summary>
    /// Returns true when a canvas logical-pixel point is inside a canvas-placed element.
    /// </summary>
    private static bool IsOverCanvasElement(FrameworkElement el, Point canvasPos)
    {
        double left = Canvas.GetLeft(el);
        double top  = Canvas.GetTop(el);
        return canvasPos.X >= left && canvasPos.X <= left + el.ActualWidth
            && canvasPos.Y >= top  && canvasPos.Y <= top  + el.ActualHeight;
    }

    /// <summary>
    /// Creates a small icon-button using Segoe MDL2 Assets glyphs.
    /// Highlights white on hover; delegates click to <paramref name="onClick"/>.
    /// </summary>
    /// <summary>
    /// Creates a copy-icon button that briefly shows an animated green tick on click
    /// before fading back to the copy icon.
    /// </summary>
    private static TextBlock MakeCopyButton(Action copyAction)
    {
        const string CopyIcon  = "\uE8C8";   // Copy
        const string CheckIcon = "\uE73E";   // Accept / checkmark

        var dimFg = new SolidColorBrush(Color.FromArgb(180, 200, 200, 220));
        var tb = new TextBlock
        {
            Text              = CopyIcon,
            FontFamily        = new FontFamily("Segoe MDL2 Assets"),
            FontSize          = 12,
            Foreground        = dimFg,
            Cursor            = Cursors.Hand,
            Padding           = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };

        bool[] animating = { false };

        tb.MouseEnter += (_, _) => { if (!animating[0]) tb.Foreground = Brushes.White; };
        tb.MouseLeave += (_, _) => { if (!animating[0]) tb.Foreground = dimFg; };
        tb.MouseLeftButtonDown += (_, e2) =>
        {
            copyAction();
            e2.Handled   = true;
            animating[0] = true;

            tb.Text = CheckIcon;

            // Animate: hold green for 350 ms, then fade to dim over 550 ms
            var animBrush = new SolidColorBrush(Color.FromArgb(255, 80, 210, 100));
            tb.Foreground = animBrush;

            var anim = new ColorAnimation
            {
                From         = Color.FromArgb(255, 80, 210, 100),
                To           = Color.FromArgb(180, 200, 200, 220),
                BeginTime    = TimeSpan.FromMilliseconds(350),
                Duration     = new Duration(TimeSpan.FromMilliseconds(550)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.HoldEnd,
            };

            anim.Completed += (_, _) =>
            {
                animBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                tb.Text       = CopyIcon;
                tb.Foreground = dimFg;
                animating[0]  = false;
            };

            animBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        };

        return tb;
    }

    private static TextBlock MakeIconButton(string icon, Action onClick)
    {
        var dimFg  = new SolidColorBrush(Color.FromArgb(180, 200, 200, 220));
        var litFg  = Brushes.White;
        var tb = new TextBlock
        {
            Text              = icon,
            FontFamily        = new FontFamily("Segoe MDL2 Assets"),
            FontSize          = 12,
            Foreground        = dimFg,
            Cursor            = Cursors.Hand,
            Padding           = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        tb.MouseLeftButtonDown += (_, e2) => { onClick(); e2.Handled = true; };
        tb.MouseEnter += (_, _) => tb.Foreground = litFg;
        tb.MouseLeave += (_, _) => tb.Foreground = dimFg;
        return tb;
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
