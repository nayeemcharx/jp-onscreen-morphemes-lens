using JapaneseOcr.Models;
using JapaneseOcr.Processing;
using Xunit;

namespace JapaneseOcr.Tests;

/// <summary>
/// Tests for <see cref="OverlayPostProcessor.RemoveDuplicates"/> and
/// <see cref="OverlayPostProcessor.RemoveTinyBoxes"/>.
/// </summary>
public sealed class DuplicateOverlayRemovalTests
{
    private static WordOverlay MakeOverlay(string text, PixelRect box)
        => new()
        {
            SurfaceText       = text,
            DictionaryForm    = text,
            ScreenBoundingBox = box,
        };

    // ── RemoveDuplicates ──────────────────────────────────────────────────────

    [Fact]
    public void RemoveDuplicates_EmptyList_ReturnsEmpty()
    {
        Assert.Empty(OverlayPostProcessor.RemoveDuplicates([]));
    }

    [Fact]
    public void RemoveDuplicates_NoDuplicates_ReturnsAll()
    {
        var overlays = new List<WordOverlay>
        {
            MakeOverlay("日本", new PixelRect(  0, 0, 50, 20)),
            MakeOverlay("語",   new PixelRect( 60, 0, 20, 20)),
            MakeOverlay("ABC",  new PixelRect(100, 0, 40, 20)),
        };

        var result = OverlayPostProcessor.RemoveDuplicates(overlays);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void RemoveDuplicates_IdenticalBoxAndText_KeepsFirst()
    {
        var box = new PixelRect(10, 10, 40, 20);
        var overlays = new List<WordOverlay>
        {
            MakeOverlay("日本", box),
            MakeOverlay("日本", box), // exact duplicate
        };

        var result = OverlayPostProcessor.RemoveDuplicates(overlays);
        Assert.Single(result);
        Assert.Equal("日本", result[0].SurfaceText);
    }

    [Fact]
    public void RemoveDuplicates_HighIoUDuplicateText_Removed()
    {
        // Box A and Box B overlap with IoU > 0.75 and have the same text
        var boxA = new PixelRect(0, 0, 100, 20);
        var boxB = new PixelRect(2, 0, 100, 20); // shifted by 2px → IoU ≈ 0.98

        var overlays = new List<WordOverlay>
        {
            MakeOverlay("言葉", boxA),
            MakeOverlay("言葉", boxB),
        };

        var result = OverlayPostProcessor.RemoveDuplicates(overlays);
        Assert.Single(result);
    }

    [Fact]
    public void RemoveDuplicates_SameTextDifferentLocation_BothKept()
    {
        // Same word appears in two separate screen regions (e.g. two menus)
        var overlays = new List<WordOverlay>
        {
            MakeOverlay("日本", new PixelRect(  0, 0, 50, 20)),
            MakeOverlay("日本", new PixelRect(500, 0, 50, 20)), // far away
        };

        var result = OverlayPostProcessor.RemoveDuplicates(overlays);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void RemoveDuplicates_DifferentTextHighOverlap_BothKept()
    {
        // Two different words with almost identical boxes → both kept
        var box = new PixelRect(10, 10, 40, 20);
        var overlays = new List<WordOverlay>
        {
            MakeOverlay("日本", box),
            MakeOverlay("語",   box),
        };

        var result = OverlayPostProcessor.RemoveDuplicates(overlays);
        Assert.Equal(2, result.Count);
    }

    // ── RemoveTinyBoxes ───────────────────────────────────────────────────────

    [Fact]
    public void RemoveTinyBoxes_TinyBoxes_Removed()
    {
        var overlays = new List<WordOverlay>
        {
            MakeOverlay("A", new PixelRect(0, 0,  3,  3)),   // 3×3 — too small
            MakeOverlay("B", new PixelRect(0, 0,  4,  4)),   // 4×4 — minimum OK
            MakeOverlay("C", new PixelRect(0, 0, 10, 10)),   // large — OK
        };

        var result = OverlayPostProcessor.RemoveTinyBoxes(overlays);
        Assert.Equal(2, result.Count);
        Assert.All(result, o =>
        {
            Assert.True(o.ScreenBoundingBox.Width  >= 4);
            Assert.True(o.ScreenBoundingBox.Height >= 4);
        });
    }

    [Fact]
    public void RemoveTinyBoxes_EmptyList_ReturnsEmpty()
    {
        Assert.Empty(OverlayPostProcessor.RemoveTinyBoxes([]));
    }
}
