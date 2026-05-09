using JPLens.Interfaces;
using JPLens.Models;
using JPLens.Processing;
using JPLens.Services;
using Microsoft.Extensions.Logging;

/// <summary>
/// Orchestrates the full OCR-overlay pipeline triggered by the global hotkey.
///
/// Thread safety: RunOcrOverlayFlowAsync is safe to call from the UI thread.
/// A volatile guard prevents overlapping runs — additional hotkey presses while
/// OCR is running are silently ignored.
/// </summary>
public sealed class AppController
{
    private readonly IScreenCaptureService              _screenCapture;
    private readonly IOcrService                        _ocrService;
    private readonly IJapaneseTokenizer                 _tokenizer;
    private readonly ITokenBoxMapper                    _tokenBoxMapper;
    private readonly IOverlayWindow                     _overlayWindow;
    private readonly IClipboardService                  _clipboard;
    private readonly AppSettings                        _settings;
    private readonly TrayIconService                    _tray;
    private readonly ILogger<AppController>             _logger;

    // State for toggle / cancellation
    private volatile int             _isProcessing;
    private CancellationTokenSource? _cts;

    public AppController(
        IScreenCaptureService   screenCapture,
        IOcrService             ocrService,
        IJapaneseTokenizer      tokenizer,
        ITokenBoxMapper         tokenBoxMapper,
        IOverlayWindow          overlayWindow,
        IClipboardService       clipboard,
        AppSettings             settings,
        TrayIconService         tray,
        ILogger<AppController>  logger)
    {
        _screenCapture  = screenCapture;
        _ocrService     = ocrService;
        _tokenizer      = tokenizer;
        _tokenBoxMapper = tokenBoxMapper;
        _overlayWindow  = overlayWindow;
        _clipboard      = clipboard;
        _settings       = settings;
        _tray           = tray;
        _logger         = logger;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Main flow — toggle
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Toggles the overlay on/off.
    /// • If the overlay is visible OR a scan is in progress → cancel/clear immediately.
    /// • If the overlay is hidden and idle → run the full OCR pipeline.
    /// Pressing the hotkey twice always produces a clean state before re-running.
    /// </summary>
    public async Task ToggleAsync()
    {
        // If visible or busy → treat as "turn off"
        if (_overlayWindow.IsVisible ||
            System.Threading.Interlocked.CompareExchange(ref _isProcessing, 0, 0) == 1)
        {
            CancelAndClear();
            return;
        }

        // Concurrency guard — only one scan at a time
        if (System.Threading.Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0)
        {
            // Race: another call won — treat this as toggle-off
            CancelAndClear();
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;

        try
        {
            await ExecuteFlowAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _overlayWindow.HideOverlay();
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _isProcessing, 0);
            if (ReferenceEquals(_cts, cts))
                _cts = null;
            cts.Dispose();
        }
    }

    /// <summary>Cancels any in-flight scan and clears the overlay immediately.</summary>
    private void CancelAndClear()
    {
        var cts = System.Threading.Interlocked.Exchange(ref _cts, null);
        cts?.Cancel();
        // Do NOT dispose here — ToggleAsync's finally block owns the lifetime
        // of the CTS and will dispose it after the awaited task unwinds.
        // Disposing here would cause a double-dispose when the finally runs.
        _overlayWindow.HideOverlay();
        _logger.LogDebug("Overlay cleared by toggle-off");
    }

    private async Task ExecuteFlowAsync(CancellationToken ct)
    {
        // Clear any leftover overlays from a previous run immediately,
        // before the scan starts — so the user never sees stale boxes.
        _overlayWindow.HideOverlay();

        // Wait for DWM to composite the overlay removal before capturing.
        // Without this delay, the GDI BitBlt can race with DWM and capture
        // the old overlay boxes still drawn on screen, causing the OCR to
        // detect them and reproduce the previous run's boxes.
        await Task.Delay(200, ct);

        _logger.LogInformation("Starting OCR overlay flow");

        // Step 2 — capture screen (GDI is fast; run on background thread to
        //           avoid blocking the WPF dispatcher)
        ScreenFrame frame;
        try
        {
            frame = await Task.Run(() => _screenCapture.CapturePrimaryMonitor(), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Screen capture failed");
            _tray.ShowBalloon("JPLens", "Screen capture failed.", Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Error);
            return;
        }

        using (frame)
        {
            // Step 3 — run OCR asynchronously (may take 0.5–3 s depending on image size)
            OcrResult ocrResult;
            try
            {
                ocrResult = await _ocrService.DetectJapaneseTextAsync(frame, ct);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("OCR cancelled");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OCR failed");
                _tray.ShowBalloon("JPLens", $"OCR error: {ex.Message}", Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Error);
                return;
            }

            ct.ThrowIfCancellationRequested();

            // Steps 4–5 — tokenize + map boxes (CPU-bound, run on thread pool)
            var overlays = await Task.Run(
                () => BuildOverlays(frame, ocrResult), ct);

            _logger.LogInformation("Built {Count} word overlays", overlays.Count);

            // Step 6 — show overlays (must be on UI thread)
            _overlayWindow.ShowOverlays(overlays);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Overlay construction (runs on background thread)
    // ──────────────────────────────────────────────────────────────────────────

    private List<WordOverlay> BuildOverlays(ScreenFrame frame, OcrResult ocrResult)
    {
        var rawOverlays = new List<WordOverlay>();

        foreach (var line in ocrResult.Lines)
        {
            _logger.LogDebug("Processing line '{T}' (confidence={C:F2})", line.Text, line.Confidence);
            // Skip low-confidence lines
            if (line.Confidence < _settings.MinimumOcrConfidence)
            {
                _logger.LogDebug("Skipping line '{T}' (confidence={C:F2})", line.Text, line.Confidence);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line.Text))
                continue;

            // Tokenize the normalized line text
            var tokens = _tokenizer.Tokenize(line.Text);

            // Map tokens to screen bounding boxes
            var lineOverlays = _tokenBoxMapper.MapTokensToBoxes(
                line, tokens, frame, ocrResult.OcrScale);

            rawOverlays.AddRange(lineOverlays);
        }

        // Post-processing: deduplicate and drop sub-pixel boxes
        var deduped = OverlayPostProcessor.RemoveDuplicates(rawOverlays);
        return OverlayPostProcessor.RemoveTinyBoxes(deduped);
    }
}
