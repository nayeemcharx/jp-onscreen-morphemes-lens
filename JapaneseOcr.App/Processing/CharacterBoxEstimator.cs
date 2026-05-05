using JapaneseOcr.Models;

namespace JapaneseOcr.Processing;

/// <summary>
/// Estimates per-character bounding boxes from a line-level bounding box when
/// the OCR engine does not provide character-level results.
///
/// Algorithm: divide the line bounding box proportionally by character count.
/// This is an approximation and works best for monospaced fonts. For
/// proportional fonts the positions will be approximate but still allow
/// reasonable word-level box union.
/// </summary>
public static class CharacterBoxEstimator
{
    /// <summary>
    /// Produces one <see cref="OcrCharacter"/> per character in
    /// <paramref name="text"/>, distributing the line bounding box evenly.
    /// Returns an empty list when <paramref name="text"/> is empty.
    /// </summary>
    public static List<OcrCharacter> Estimate(
        string         text,
        PixelRect      lineBounds,
        TextOrientation orientation)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        // Use StringInfo to count grapheme clusters (handles surrogate pairs)
        var elements   = GetTextElements(text);
        int totalChars = elements.Count;

        if (totalChars == 0)
            return [];

        var result = new List<OcrCharacter>(totalChars);

        for (int i = 0; i < totalChars; i++)
        {
            double startRatio = (double)i       / totalChars;
            double endRatio   = (double)(i + 1) / totalChars;

            PixelRect charBox = orientation == TextOrientation.Vertical
                ? new PixelRect(
                    lineBounds.X,
                    lineBounds.Y + lineBounds.Height * startRatio,
                    lineBounds.Width,
                    lineBounds.Height * (endRatio - startRatio))
                : new PixelRect(
                    lineBounds.X + lineBounds.Width * startRatio,
                    lineBounds.Y,
                    lineBounds.Width * (endRatio - startRatio),
                    lineBounds.Height);

            result.Add(new OcrCharacter
            {
                Text        = elements[i],
                BoundingBox = charBox,
                // Estimated boxes carry no per-character confidence
                Confidence  = 0.0,
            });
        }

        return result;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Internal helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Splits text into grapheme clusters using <see cref="System.Globalization.StringInfo"/>.
    /// Each element is one printable unit (handles CJK supplementary characters).
    /// </summary>
    internal static List<string> GetTextElements(string text)
    {
        var si      = new System.Globalization.StringInfo(text);
        var result  = new List<string>(si.LengthInTextElements);
        for (int i = 0; i < si.LengthInTextElements; i++)
            result.Add(si.SubstringByTextElements(i, 1));
        return result;
    }
}
