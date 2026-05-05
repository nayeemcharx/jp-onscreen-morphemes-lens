using JapaneseOcr.Models;
using JapaneseOcr.Processing;
using Xunit;

namespace JapaneseOcr.Tests;

/// <summary>
/// Tests for <see cref="GeometryHelper.Union"/> and related rectangle utilities.
/// </summary>
public sealed class RectangleUnionTests
{
    // ── Union ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Union_EmptyList_ReturnsDefault()
    {
        var result = GeometryHelper.Union([]);
        Assert.Equal(0, result.Width);
        Assert.Equal(0, result.Height);
    }

    [Fact]
    public void Union_SingleRect_ReturnsSameRect()
    {
        var r      = new PixelRect(10, 20, 30, 40);
        var result = GeometryHelper.Union([r]);
        AssertRectEqual(r, result);
    }

    [Fact]
    public void Union_NonOverlappingRects_EnclosesAll()
    {
        var a = new PixelRect(0,  0, 10, 10);
        var b = new PixelRect(20, 0, 10, 10);
        var u = GeometryHelper.Union([a, b]);

        Assert.Equal(0,  u.X);
        Assert.Equal(0,  u.Y);
        Assert.Equal(30, u.Width);   // 0 to 30
        Assert.Equal(10, u.Height);
    }

    [Fact]
    public void Union_OverlappingRects_CorrectBounds()
    {
        var a = new PixelRect(5, 5,  20, 20); // right=25, bottom=25
        var b = new PixelRect(10, 10, 30, 30); // right=40, bottom=40
        var u = GeometryHelper.Union([a, b]);

        Assert.Equal(5,  u.X);
        Assert.Equal(5,  u.Y);
        Assert.Equal(35, u.Width);  // 40 - 5
        Assert.Equal(35, u.Height); // 40 - 5
    }

    [Fact]
    public void Union_ThreeRects_ContainsAllPoints()
    {
        var rects = new[]
        {
            new PixelRect( 0,  0, 10, 10),
            new PixelRect( 5,  5, 10, 10),
            new PixelRect(-5, -5, 20, 20),
        };

        var u = GeometryHelper.Union(rects);

        // All points from all rects must be inside the union
        foreach (var r in rects)
        {
            Assert.True(u.Contains(r.X,      r.Y));
            Assert.True(u.Contains(r.Right,  r.Bottom));
        }
    }

    // ── IoU ───────────────────────────────────────────────────────────────────

    [Fact]
    public void IoU_IdenticalRects_ReturnsOne()
    {
        var r = new PixelRect(0, 0, 100, 100);
        Assert.Equal(1.0, GeometryHelper.IoU(r, r), precision: 5);
    }

    [Fact]
    public void IoU_NonOverlapping_ReturnsZero()
    {
        var a = new PixelRect(0, 0, 10, 10);
        var b = new PixelRect(20, 0, 10, 10);
        Assert.Equal(0.0, GeometryHelper.IoU(a, b));
    }

    [Fact]
    public void IoU_PartialOverlap_BetweenZeroAndOne()
    {
        // a and b each 10×10, overlapping by 5×10 → intersection=50, union=150
        var a   = new PixelRect(0, 0, 10, 10);
        var b   = new PixelRect(5, 0, 10, 10);
        var iou = GeometryHelper.IoU(a, b);

        Assert.InRange(iou, 0.0, 1.0);
        Assert.Equal(50.0 / 150.0, iou, precision: 5);
    }

    // ── Padding ───────────────────────────────────────────────────────────────

    [Fact]
    public void AddPadding_ExpandsAllSides()
    {
        var r       = new PixelRect(10, 10, 20, 20);
        var padded  = GeometryHelper.AddPadding(r, 3.0);

        Assert.Equal(7,  padded.X);
        Assert.Equal(7,  padded.Y);
        Assert.Equal(26, padded.Width);
        Assert.Equal(26, padded.Height);
    }

    // ──────────────────────────────────────────────────────────────────────────

    private static void AssertRectEqual(PixelRect expected, PixelRect actual,
        int precision = 5)
    {
        Assert.Equal(expected.X,      actual.X,      precision);
        Assert.Equal(expected.Y,      actual.Y,      precision);
        Assert.Equal(expected.Width,  actual.Width,  precision);
        Assert.Equal(expected.Height, actual.Height, precision);
    }
}
