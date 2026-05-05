using JapaneseOcr.Models;
using JapaneseOcr.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JapaneseOcr.Tests;

/// <summary>
/// Verifies the token display-filtering logic in <see cref="TokenBoxMapper.ShouldDisplayToken"/>.
/// </summary>
public sealed class TokenFilteringTests
{
    private TokenBoxMapper CreateMapper(AppSettings? settings = null)
    {
        var s = settings ?? new AppSettings();
        return new TokenBoxMapper(s, NullLogger<TokenBoxMapper>.Instance);
    }

    // ── Null / empty ──────────────────────────────────────────────────────────

    [Fact]
    public void NullSurface_IsFiltered()
    {
        var mapper = CreateMapper();
        var token  = new JapaneseToken { SurfaceText = null! };
        Assert.False(mapper.ShouldDisplayToken(token));
    }

    [Fact]
    public void WhitespaceSurface_IsFiltered()
    {
        var mapper = CreateMapper();
        var token  = new JapaneseToken { SurfaceText = "   " };
        Assert.False(mapper.ShouldDisplayToken(token));
    }

    // ── Punctuation ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("。")]
    [InlineData("、")]
    [InlineData("？")]
    [InlineData("！")]
    [InlineData(".")]
    [InlineData(",")]
    public void Punctuation_FilteredWhenShowPunctuationDisabled(string text)
    {
        var mapper = CreateMapper(new AppSettings { ShowPunctuation = false });
        var token  = new JapaneseToken { SurfaceText = text, PartOfSpeech = "記号" };
        Assert.False(mapper.ShouldDisplayToken(token));
    }

    [Fact]
    public void Punctuation_ShownWhenShowPunctuationEnabled()
    {
        var mapper = CreateMapper(new AppSettings { ShowPunctuation = true });
        var token  = new JapaneseToken { SurfaceText = "。", PartOfSpeech = "記号" };
        Assert.True(mapper.ShouldDisplayToken(token));
    }

    // ── Particles ─────────────────────────────────────────────────────────────

    [Fact]
    public void Particle_FilteredWhenShowParticlesDisabled()
    {
        var mapper = CreateMapper(new AppSettings { ShowParticles = false });
        var token  = new JapaneseToken { SurfaceText = "の", PartOfSpeech = "助詞-格助詞" };
        Assert.False(mapper.ShouldDisplayToken(token));
    }

    [Fact]
    public void MultiCharParticle_ShownWhenShowParticlesEnabled()
    {
        var mapper = CreateMapper(new AppSettings { ShowParticles = true });
        var token  = new JapaneseToken { SurfaceText = "から", PartOfSpeech = "助詞-格助詞" };
        Assert.True(mapper.ShouldDisplayToken(token));
    }

    [Fact]
    public void SingleCharParticle_AlwaysFiltered()
    {
        // Single-char particles are noise regardless of ShowParticles setting
        var mapper = CreateMapper(new AppSettings { ShowParticles = true });
        var token  = new JapaneseToken { SurfaceText = "は", PartOfSpeech = "助詞-係助詞" };
        Assert.False(mapper.ShouldDisplayToken(token));
    }

    // ── Auxiliary symbols (補助記号) ───────────────────────────────────────────

    [Fact]
    public void AuxiliarySymbol_FilteredByDefault()
    {
        var mapper = CreateMapper(); // ShowAuxiliarySymbols defaults to false
        var token  = new JapaneseToken { SurfaceText = "。", PartOfSpeech = "補助記号-句点" };
        Assert.False(mapper.ShouldDisplayToken(token));
    }

    [Fact]
    public void AuxiliarySymbol_ShownWhenEnabled()
    {
        var mapper = CreateMapper(new AppSettings { ShowAuxiliarySymbols = true, ShowPunctuation = true });
        var token  = new JapaneseToken { SurfaceText = "。", PartOfSpeech = "補助記号-句点" };
        Assert.True(mapper.ShouldDisplayToken(token));
    }

    // ── Whitespace tokens (空白) ───────────────────────────────────────────────

    [Fact]
    public void WhitespaceToken_FilteredByDefault()
    {
        var mapper = CreateMapper();
        var token  = new JapaneseToken { SurfaceText = " ", PartOfSpeech = "空白" };
        Assert.False(mapper.ShouldDisplayToken(token));
    }

    // ── Minimum length ────────────────────────────────────────────────────────

    [Fact]
    public void TokenShorterThanMinimum_IsFiltered()
    {
        var mapper = CreateMapper(new AppSettings { MinimumTokenLength = 2 });
        var token  = new JapaneseToken { SurfaceText = "日" };
        Assert.False(mapper.ShouldDisplayToken(token));
    }

    [Fact]
    public void TokenAtMinimumLength_IsShown()
    {
        var mapper = CreateMapper(new AppSettings { MinimumTokenLength = 1 });
        var token  = new JapaneseToken { SurfaceText = "日" };
        Assert.True(mapper.ShouldDisplayToken(token));
    }

    // ── Normal tokens ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("日本語")]
    [InlineData("テスト")]
    [InlineData("hello")]
    public void NormalToken_IsShown(string text)
    {
        var mapper = CreateMapper();
        var token  = new JapaneseToken { SurfaceText = text };
        Assert.True(mapper.ShouldDisplayToken(token));
    }
}
