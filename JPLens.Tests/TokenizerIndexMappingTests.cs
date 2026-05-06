using JPLens.Models;
using JPLens.Services;
using Xunit;

namespace JPLens.Tests;

/// <summary>
/// Verifies that <see cref="StubJapaneseTokenizer"/> produces tokens whose
/// StartCharIndex / EndCharIndex correctly index into the normalized string,
/// and that the surface text matches the substring at those indices.
/// </summary>
public sealed class TokenizerIndexMappingTests
{
    private readonly StubJapaneseTokenizer _tokenizer;

    public TokenizerIndexMappingTests()
    {
        // Use NullLogger to avoid DI in tests
        _tokenizer = new StubJapaneseTokenizer(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StubJapaneseTokenizer>.Instance);
    }

    [Theory]
    [InlineData("日本語")]                      // pure kanji
    [InlineData("てすと")]                      // pure hiragana
    [InlineData("テスト")]                      // pure katakana
    [InlineData("日本語テスト")]                // kanji + katakana
    [InlineData("日本語でテスト中")]            // mixed kanji, hiragana, katakana
    [InlineData("abc123")]                      // ASCII only
    [InlineData("日本語 text 混在")]            // mixed Japanese, spaces, Latin
    public void AllTokens_IndexesMatchSubstring(string input)
    {
        var normalized = JPLens.Processing.TextNormalizer.Normalize(input);
        var tokens     = _tokenizer.Tokenize(normalized);

        Assert.All(tokens, token =>
        {
            Assert.True(token.StartCharIndex >= 0);
            Assert.True(token.EndCharIndex   >  token.StartCharIndex);
            Assert.True(token.EndCharIndex   <= normalized.Length);

            var extracted = normalized[token.StartCharIndex..token.EndCharIndex];
            Assert.Equal(token.SurfaceText, extracted);
        });
    }

    [Fact]
    public void Tokens_CoverEntireInputWithoutGaps()
    {
        const string input = "日本語でテスト";
        var normalized = JPLens.Processing.TextNormalizer.Normalize(input);
        var tokens     = _tokenizer.Tokenize(normalized);

        // Each token should start where the previous one ended
        int cursor = 0;
        foreach (var token in tokens)
        {
            Assert.Equal(cursor, token.StartCharIndex);
            cursor = token.EndCharIndex;
        }

        Assert.Equal(normalized.Length, cursor);
    }

    [Fact]
    public void EmptyInput_ReturnsEmptyList()
    {
        Assert.Empty(_tokenizer.Tokenize(""));
        Assert.Empty(_tokenizer.Tokenize("   "));
    }

    [Fact]
    public void KanjiAndHiragana_SplitIntoSeparateTokens()
    {
        // "日本語の" — kanji "日本語" then hiragana "の"
        const string input = "日本語の";
        var tokens = _tokenizer.Tokenize(input);

        Assert.True(tokens.Count >= 2,
            $"Expected ≥ 2 tokens for '{input}' but got {tokens.Count}");

        // First token should be the kanji block
        Assert.Equal("日本語", tokens[0].SurfaceText);
        Assert.Equal("の",     tokens[1].SurfaceText);
    }
}
