using System.Text.Json.Serialization;

namespace JapaneseOcr.Models;

/// <summary>
/// User-configurable application settings.
/// Stored as JSON in %AppData%\JapaneseOcr\settings.json.
/// All properties have sensible defaults; the file is created on first run.
/// </summary>
public sealed class AppSettings
{
    // ── OCR ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimum OCR line confidence [0–1]. Lines below this threshold are skipped.
    /// Lower values show more results but risk garbage text overlays.
    /// </summary>
    public double MinimumOcrConfidence { get; set; } = 0.60;

    /// <summary>
    /// Upscale factor applied to the captured frame before OCR.
    /// 1.0 = raw passthrough (recommended — identical to Win+Shift+T behaviour).
    /// The engine clamps automatically so that neither dimension exceeds 4096 px.
    /// </summary>
    public double OcrScale { get; set; } = 1.0;

    /// <summary>
    /// Contrast multiplier applied before OCR.  1.0 = no change (recommended).
    /// Values slightly above 1.0 (e.g. 1.1) can help very low-contrast text,
    /// but screen text is already high-contrast so values > 1.1 tend to clip
    /// anti-aliased edges and degrade OCR quality.
    /// </summary>
    public float OcrContrast { get; set; } = 1.0f;

    /// <summary>
    /// When true, a mild 3×3 unsharp-mask is applied after upscaling.
    /// Disabled by default — screen text is already sharp, and the kernel
    /// creates ringing around fine Japanese strokes which hurts recognition.
    /// Enable only for blurry/screenshot sources.
    /// </summary>
    public bool OcrSharpening { get; set; } = false;

    // ── Tokenizer ─────────────────────────────────────────────────────────────

    /// <summary>
    /// URL of the local SudachiPy FastAPI tokenizer server.
    /// Start the server with: uvicorn main:app --port 8000 (see tokenizer-server/).
    /// </summary>
    public string SudachiServerUrl { get; set; } = "http://localhost:8000";

    /// <summary>
    /// When false (default), punctuation-only tokens produce no overlay box.
    /// Punctuation is detected both by Unicode category and by the 補助記号 POS.
    /// </summary>
    public bool ShowPunctuation { get; set; } = false;

    /// <summary>
    /// When false (default), tokens whose main POS is 補助記号 (Sudachi's
    /// category for punctuation marks, brackets, etc.) are hidden.
    /// </summary>
    public bool ShowAuxiliarySymbols { get; set; } = false;

    /// <summary>
    /// When false (default), tokens whose main POS is 空白 (whitespace) are hidden.
    /// </summary>
    public bool ShowWhitespaceTokens { get; set; } = false;

    /// <summary>
    /// When true (default), particles (助詞) longer than one character are shown.
    /// Single-character particles (は、が、を、に etc.) are always hidden as they
    /// are too short to be useful as standalone overlay targets.
    /// Set to false to hide all particles.
    /// </summary>
    public bool ShowParticles { get; set; } = true;

    /// <summary>Tokens shorter than this many characters are not displayed.</summary>
    public int MinimumTokenLength { get; set; } = 1;

    // ── Clipboard ─────────────────────────────────────────────────────────────

    /// <summary>
    /// When true, clicking a word copies the dictionary form instead of the surface form.
    /// </summary>
    public bool CopyDictionaryForm { get; set; } = false;

    // ── Overlay ───────────────────────────────────────────────────────────────

    /// <summary>Extra padding (physical pixels) added around each token box.</summary>
    public double OverlayPadding { get; set; } = 3.0;

    // ── Global hotkey ─────────────────────────────────────────────────────────

    /// <summary>
    /// Global hotkey string. Format: "CTRL+SHIFT+J".
    /// Supported modifiers: CTRL, SHIFT, ALT, WIN.
    /// </summary>
    public string Hotkey { get; set; } = "CTRL+SHIFT+J";

    // ── Monitor ───────────────────────────────────────────────────────────────

    /// <summary>
    /// When true, only the primary monitor is captured.
    /// Multi-monitor support is designed in data structures but not yet wired up.
    /// </summary>
    public bool PrimaryMonitorOnly { get; set; } = true;
}
