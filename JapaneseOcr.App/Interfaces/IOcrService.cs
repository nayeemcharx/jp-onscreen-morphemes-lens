using JapaneseOcr.Models;

namespace JapaneseOcr.Interfaces;

/// <summary>
/// Runs OCR on a captured screen frame and returns structured line/character results.
/// Implementations can back this interface with:
///   - Windows.Media.Ocr  (built-in, no install required)
///   - PaddleOCR/ONNX     (local subprocess or embedded model)
///   - External local OCR server (HTTP or named-pipe)
/// </summary>
public interface IOcrService
{
    /// <summary>
    /// Detects Japanese text in <paramref name="frame"/> and returns an
    /// <see cref="OcrResult"/> containing line and (optionally) character bounding boxes.
    /// Bounding boxes are in the upscaled OCR-image coordinate space;
    /// divide by <see cref="OcrResult.OcrScale"/> to convert to screen pixels.
    /// </summary>
    Task<OcrResult> DetectJapaneseTextAsync(ScreenFrame frame, CancellationToken ct = default);
}
