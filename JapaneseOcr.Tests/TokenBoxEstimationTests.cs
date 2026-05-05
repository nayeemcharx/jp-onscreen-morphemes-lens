using JapaneseOcr.Models;
using JapaneseOcr.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JapaneseOcr.Tests;

/// <summary>
/// Tests for <see cref="TokenBoxMapper.EstimateTokenBox"/> and the
/// fallback estimation path in the mapping pipeline.
/// </summary>
public sealed class TokenBoxEstimationTests
{
    private static OcrLine MakeLine(string text, PixelRect box,
        TextOrientation orientation = TextOrientation.Horizontal)
        => new()
        {
            Text        = text,
            BoundingBox = box,
            Confidence  = 1.0,
            Characters  = [],           // empty → forces fallback estimation
            Orientation = orientation,
        };

    private static JapaneseToken MakeToken(string surface, int start, int end)
        => new() { SurfaceText = surface, DictionaryForm = surface, StartCharIndex = start, EndCharIndex = end };

    // ── Horizontal text ───────────────────────────────────────────────────────

    [Fact]
    public void Horizontal_FirstToken_StartsAtLineLeft()
    {
        // Line "ABCD" (4 chars), box x=0 w=100
        // Token "AB" → chars 0–2 → x should start at 0, width ≈ 50
        var line  = MakeLine("ABCD", new PixelRect(0, 0, 100, 20));
        var token = MakeToken("AB", 0, 2);
        var box   = TokenBoxMapper.EstimateTokenBox(line, token);

        Assert.Equal(0,  box.X,      precision: 5);
        Assert.Equal(50, box.Width,  precision: 5);
        Assert.Equal(0,  box.Y,      precision: 5);
        Assert.Equal(20, box.Height, precision: 5);
    }

    [Fact]
    public void Horizontal_LastToken_EndsAtLineRight()
    {
        var line  = MakeLine("ABCD", new PixelRect(0, 0, 100, 20));
        var token = MakeToken("CD", 2, 4);
        var box   = TokenBoxMapper.EstimateTokenBox(line, token);

        Assert.Equal(50,  box.X,     precision: 5); // 2/4 * 100 = 50
        Assert.Equal(50,  box.Width, precision: 5); // (4-2)/4 * 100 = 50
    }

    [Fact]
    public void Horizontal_MonitorOffset_IsAppliedCorrectly()
    {
        // The EstimateTokenBox method works in OCR-image space (no monitor offset).
        // Monitor offset is applied separately in MapTokensToBoxes.
        var line  = MakeLine("ABC", new PixelRect(10, 5, 90, 15));
        var token = MakeToken("A", 0, 1);
        var box   = TokenBoxMapper.EstimateTokenBox(line, token);

        // Start X should be line.X + 0/3 * line.Width
        Assert.Equal(10, box.X, precision: 5);
        Assert.Equal(30, box.Width, precision: 5); // 1/3 * 90 = 30
    }

    // ── Vertical text ─────────────────────────────────────────────────────────

    [Fact]
    public void Vertical_TokenHeight_ProportionalToLineHeight()
    {
        var line  = MakeLine("ABCD", new PixelRect(0, 0, 20, 100), TextOrientation.Vertical);
        var token = MakeToken("AB", 0, 2);
        var box   = TokenBoxMapper.EstimateTokenBox(line, token);

        Assert.Equal(0,  box.Y,      precision: 5);
        Assert.Equal(50, box.Height, precision: 5); // 2/4 * 100 = 50
        Assert.Equal(20, box.Width,  precision: 5); // full line width
    }

    // ── Mapper pipeline (no char boxes) ──────────────────────────────────────

    [Fact]
    public void MapTokensToBoxes_NoCharBoxes_ProducesOverlaysViaEstimation()
    {
        var settings = new AppSettings { MinimumTokenLength = 1, ShowPunctuation = true };
        var mapper   = new TokenBoxMapper(settings,
            NullLogger<TokenBoxMapper>.Instance);

        var line = MakeLine("日本語", new PixelRect(0, 0, 90, 20));

        // Tokenize "日本語" into one token covering the whole string
        var token = MakeToken("日本語", 0, 3);

        // Minimal ScreenFrame with no DPI offset
        using var frame = new ScreenFrame(
            image:       new System.Drawing.Bitmap(1, 1),
            width:       100, height: 100,
            monitorX:    0,   monitorY: 0,
            scaleFactor: 1.0,
            capturedAt:  DateTime.UtcNow);

        var overlays = mapper.MapTokensToBoxes(line, [token], frame, ocrScale: 1.0);

        Assert.Single(overlays);
        Assert.Equal("日本語", overlays[0].SurfaceText);
        Assert.True(overlays[0].ScreenBoundingBox.IsValid);
    }
}
