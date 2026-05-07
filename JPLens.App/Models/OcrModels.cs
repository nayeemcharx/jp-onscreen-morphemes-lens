namespace JPLens.Models;

// ──────────────────────────────────────────────────────────────────────────────
// OCR data models
// Coordinates in these models are in the pixel space of the image fed to the
// OCR engine (which may be upscaled). TokenBoxMapper scales them back to screen
// pixel coordinates before storing them in WordOverlay.
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>Reading direction of a text line.</summary>
public enum TextOrientation
{
    Horizontal,
    Vertical,
    Unknown
}

/// <summary>
/// A single character (or short character cluster) recognized by the OCR engine,
/// along with its bounding box in the OCR image's coordinate space.
/// </summary>
public sealed class OcrCharacter
{
    public string     Text        { get; init; } = string.Empty;
    public PixelRect  BoundingBox { get; init; }
    public double     Confidence  { get; init; }
}

/// <summary>
/// A line of text produced by the OCR engine.
/// <see cref="Characters"/> contains individual character/cluster boxes when the
/// engine returns them; otherwise <see cref="Characters"/> is empty and
/// <see cref="CharacterBoxEstimator"/> fills in estimated positions.
/// </summary>
public sealed class OcrLine
{
    /// <summary>Full normalized text of the line.</summary>
    public string               Text        { get; init; } = string.Empty;

    /// <summary>Bounding box of the entire line in OCR-image pixel space.</summary>
    public PixelRect            BoundingBox { get; init; }

    /// <summary>0–1 confidence from the OCR engine.</summary>
    public double               Confidence  { get; init; }

    /// <summary>
    /// Per-character (or per-word cluster) bounding boxes.
    /// May be empty if the engine only returns line-level results.
    /// </summary>
    public List<OcrCharacter>   Characters  { get; init; } = [];

    /// <summary>Detected reading direction.</summary>
    public TextOrientation      Orientation { get; init; } = TextOrientation.Horizontal;
}

/// <summary>
/// Full result returned by an <see cref="Interfaces.IOcrService"/> implementation.
/// </summary>
public sealed class OcrResult
{
    public List<OcrLine> Lines    { get; init; } = [];

    /// <summary>
    /// The scale factor applied to the source image before OCR.
    /// All <see cref="OcrLine.BoundingBox"/> coordinates are in the
    /// upscaled coordinate space (source × OcrScale).
    /// Divide by this value to convert back to native screen pixels.
    /// </summary>
    public double        OcrScale { get; init; } = 1.0;
}
