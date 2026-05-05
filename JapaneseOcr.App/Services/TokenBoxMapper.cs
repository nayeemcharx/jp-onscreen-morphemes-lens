using JapaneseOcr.Interfaces;
using JapaneseOcr.Models;
using JapaneseOcr.Processing;
using Microsoft.Extensions.Logging;

namespace JapaneseOcr.Services;

/// <summary>
/// Maps tokenizer output back to physical screen bounding boxes by correlating
/// token character indices with the per-character OCR boxes in each line.
///
/// Coordinate pipeline:
///   1. OCR character boxes are in the upscaled OCR-image space.
///   2. Divide by <paramref name="ocrScale"/> → native screen pixels relative to monitor (0,0).
///   3. Add monitor offset → absolute physical screen pixels.
///   4. Add padding from <see cref="AppSettings.OverlayPadding"/>.
///
/// Token display filtering (ShouldDisplayToken) is applied here so the list
/// returned from MapTokensToBoxes contains only visible overlays.
/// </summary>
public sealed class TokenBoxMapper : ITokenBoxMapper
{
    private readonly AppSettings                _settings;
    private readonly ILogger<TokenBoxMapper>    _logger;

    public TokenBoxMapper(AppSettings settings, ILogger<TokenBoxMapper> logger)
    {
        _settings = settings;
        _logger   = logger;
    }

    /// <inheritdoc/>
    public List<WordOverlay> MapTokensToBoxes(
        OcrLine             line,
        List<JapaneseToken> tokens,
        ScreenFrame         frame,
        double              ocrScale)
    {
        var overlays = new List<WordOverlay>(tokens.Count);

        // Build a map: char index in line.Text → OcrCharacter index
        // Each OcrCharacter may cover one or several characters.
        var charToOcrIndex = BuildCharToOcrIndexMap(line);

        foreach (var token in tokens)
        {
            if (!ShouldDisplayToken(token))
                continue;

            // Collect the OCR character boxes that fall within this token's range
            var charBoxes = new List<PixelRect>();
            var seen      = new HashSet<int>(); // avoid adding the same OcrChar twice

            for (int ci = token.StartCharIndex; ci < token.EndCharIndex; ci++)
            {
                if (charToOcrIndex.TryGetValue(ci, out int ocrIdx) &&
                    seen.Add(ocrIdx) &&
                    ocrIdx < line.Characters.Count)
                {
                    charBoxes.Add(line.Characters[ocrIdx].BoundingBox);
                }
            }

            PixelRect rawBox;

            if (charBoxes.Count > 0)
            {
                rawBox = GeometryHelper.Union(charBoxes);
            }
            else
            {
                // Fallback: estimate from the line bounding box using character ratios
                _logger.LogDebug(
                    "No char boxes for token '{T}' [{S}-{E}]; using estimation.",
                    token.SurfaceText, token.StartCharIndex, token.EndCharIndex);

                rawBox = EstimateTokenBox(line, token);
            }

            // Scale back from OCR-image space → native screen pixels
            var scaledBox = GeometryHelper.Scale(rawBox, 1.0 / ocrScale);

            // Translate from monitor-relative → absolute virtual screen coordinates
            var screenBox = GeometryHelper.Translate(scaledBox, frame.MonitorX, frame.MonitorY);

            // Add per-overlay padding
            var paddedBox = GeometryHelper.AddPadding(screenBox, _settings.OverlayPadding);

            if (!paddedBox.IsValid)
                continue;

            overlays.Add(new WordOverlay
            {
                SurfaceText       = token.SurfaceText,
                DictionaryForm    = token.DictionaryForm,
                PartOfSpeech      = token.PartOfSpeech,
                ScreenBoundingBox = paddedBox,
                SourceLineText    = line.Text,
                Confidence        = line.Confidence,
            });
        }

        return overlays;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Token display filtering
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Decides whether a token should produce a visible overlay box.
    ///
    /// Default behaviour (all AppSettings at their defaults):
    ///   • Punctuation-only text  → hidden
    ///   • 補助記号 POS           → hidden  (Sudachi's punctuation category)
    ///   • 空白 POS               → hidden  (whitespace tokens)
    ///   • Single-char 助詞       → hidden  (は, が, を, に, etc. are noise)
    ///   • Everything else        → shown
    ///
    /// All rules are individually configurable via <see cref="AppSettings"/>.
    /// </summary>
    public bool ShouldDisplayToken(JapaneseToken token)
    {
        if (string.IsNullOrWhiteSpace(token.SurfaceText))
            return false;

        // ── Punctuation (text-content based) ──────────────────────────────────
        if (!_settings.ShowPunctuation && IsPunctuation(token.SurfaceText))
            return false;

        // ── 補助記号 — Sudachi's POS for punctuation/brackets/etc. ────────────
        if (!_settings.ShowAuxiliarySymbols &&
            token.PartOfSpeech.StartsWith("補助記号", StringComparison.Ordinal))
            return false;

        // ── 空白 — whitespace tokens ──────────────────────────────────────────
        if (!_settings.ShowWhitespaceTokens &&
            token.PartOfSpeech.StartsWith("空白", StringComparison.Ordinal))
            return false;

        // ── Particles (助詞) ───────────────────────────────────────────────────
        bool isParticle = token.PartOfSpeech.StartsWith("助詞", StringComparison.Ordinal);

        if (isParticle)
        {
            // Hide all particles when ShowParticles is disabled.
            if (!_settings.ShowParticles)
                return false;

            // Single-character particles (は, が, を, に, で, と, …) are
            // always hidden even when ShowParticles=true — they are too short
            // to be meaningful overlay targets.
            if (token.SurfaceText.Length == 1)
                return false;
        }

        // ── Minimum length ────────────────────────────────────────────────────
        if (token.SurfaceText.Length < _settings.MinimumTokenLength)
            return false;

        return true;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Internal helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a mapping from character position in line.Text → OcrCharacter index.
    /// Each OcrCharacter.Text may cover multiple consecutive characters.
    /// </summary>
    private static Dictionary<int, int> BuildCharToOcrIndexMap(OcrLine line)
    {
        var map    = new Dictionary<int, int>(line.Text.Length);
        int cursor = 0;

        for (int ocrIdx = 0; ocrIdx < line.Characters.Count; ocrIdx++)
        {
            var ch   = line.Characters[ocrIdx];
            int len  = ch.Text.Length;

            for (int k = 0; k < len && cursor + k < line.Text.Length; k++)
                map[cursor + k] = ocrIdx;

            cursor += len;
            if (cursor >= line.Text.Length)
                break;
        }

        return map;
    }

    /// <summary>
    /// Estimates a token bounding box by proportional division of the line box.
    /// Works best for horizontal monospaced text.
    /// </summary>
    internal static PixelRect EstimateTokenBox(OcrLine line, JapaneseToken token)
    {
        var si         = new System.Globalization.StringInfo(line.Text);
        int totalElems = si.LengthInTextElements;

        if (totalElems == 0)
            return line.BoundingBox;

        double startRatio = (double)token.StartCharIndex / totalElems;
        double endRatio   = (double)token.EndCharIndex   / totalElems;

        return line.Orientation == TextOrientation.Vertical
            ? new PixelRect(
                line.BoundingBox.X,
                line.BoundingBox.Y + line.BoundingBox.Height * startRatio,
                line.BoundingBox.Width,
                line.BoundingBox.Height * (endRatio - startRatio))
            : new PixelRect(
                line.BoundingBox.X + line.BoundingBox.Width * startRatio,
                line.BoundingBox.Y,
                line.BoundingBox.Width * (endRatio - startRatio),
                line.BoundingBox.Height);
    }

    private static bool IsPunctuation(string text)
    {
        // All characters are punctuation/symbol/whitespace
        foreach (char c in text)
        {
            if (!char.IsPunctuation(c) && !char.IsSymbol(c) && !char.IsWhiteSpace(c))
                return false;
        }
        return true;
    }
}
