using JPLens.Interfaces;
using JPLens.Models;
using JPLens.Processing;
using Microsoft.Extensions.Logging;

namespace JPLens.Services;

/// <summary>
/// A built-in Japanese tokenizer that groups consecutive characters of the
/// same Unicode script into tokens.
///
/// This is an MVP stand-in that requires no external dependencies. It produces
/// reasonable word-like segments for pure Japanese text but does not perform
/// morphological analysis (i.e. it does not split compound verbs, conjugations,
/// or mixed kanji+kana words).
///
/// REPLACING THIS TOKENIZER
/// ─────────────────────────
/// Implement <see cref="IJapaneseTokenizer"/> and register the new class in
/// <c>App.xaml.cs</c>:
///   services.AddSingleton&lt;IJapaneseTokenizer, MeCabTokenizer&gt;();
///
/// MeCab example (subprocess approach):
///   - Install MeCab + IPAdic: chocolatey install mecab
///   - Pipe text to the mecab.exe process, parse tab-separated output
///   - Map surface/dictionary form/POS from the MeCab columns
///
/// Sudachi example:
///   - Run a local SudachiPy HTTP server or use the Java CLI
///   - Call it via HttpClient and parse JSON results
/// </summary>
public sealed class StubJapaneseTokenizer : IJapaneseTokenizer
{
    private readonly ILogger<StubJapaneseTokenizer> _logger;

    public StubJapaneseTokenizer(ILogger<StubJapaneseTokenizer> logger)
        => _logger = logger;

    /// <inheritdoc/>
    public List<JapaneseToken> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var normalized = TextNormalizer.Normalize(text);
        var tokens     = new List<JapaneseToken>();

        // Walk the string, grouping consecutive characters with the same
        // script/category into a single token.
        int start = 0;

        while (start < normalized.Length)
        {
            var category = ClassifyChar(normalized[start]);
            int end      = start + 1;

            while (end < normalized.Length && ClassifyChar(normalized[end]) == category)
                end++;

            var surface = normalized[start..end];

            tokens.Add(new JapaneseToken
            {
                SurfaceText    = surface,
                DictionaryForm = surface,   // stub: no morphological lookup
                PartOfSpeech   = category.ToJapanesePosLabel(),
                StartCharIndex = start,
                EndCharIndex   = end,
            });

            start = end;
        }

        _logger.LogDebug("Tokenized '{Text}' → {Count} tokens", normalized, tokens.Count);
        return tokens;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Unicode script classification
    // ──────────────────────────────────────────────────────────────────────────

    internal enum ScriptCategory
    {
        Kanji,
        Hiragana,
        Katakana,
        Latin,
        Digit,
        Punctuation,
        Whitespace,
        Other,
    }

    internal static ScriptCategory ClassifyChar(char c)
    {
        // CJK Unified Ideographs (basic block)
        if (c >= 0x4E00 && c <= 0x9FFF) return ScriptCategory.Kanji;
        // CJK Extension A
        if (c >= 0x3400 && c <= 0x4DBF) return ScriptCategory.Kanji;
        // CJK Compatibility Ideographs
        if (c >= 0xF900 && c <= 0xFAFF) return ScriptCategory.Kanji;

        // Hiragana (includes small forms and combining marks)
        if (c >= 0x3040 && c <= 0x309F) return ScriptCategory.Hiragana;

        // Katakana (full-width)
        if (c >= 0x30A0 && c <= 0x30FF) return ScriptCategory.Katakana;
        // Katakana Phonetic Extensions
        if (c >= 0x31F0 && c <= 0x31FF) return ScriptCategory.Katakana;

        if (char.IsLetter(c)) return ScriptCategory.Latin;
        if (char.IsDigit(c))  return ScriptCategory.Digit;
        if (char.IsWhiteSpace(c)) return ScriptCategory.Whitespace;
        if (char.IsPunctuation(c) || char.IsSymbol(c)) return ScriptCategory.Punctuation;

        return ScriptCategory.Other;
    }
}

file static class ScriptCategoryExtensions
{
    public static string ToJapanesePosLabel(this StubJapaneseTokenizer.ScriptCategory c)
        => c switch
        {
            StubJapaneseTokenizer.ScriptCategory.Kanji       => "名詞",   // noun (placeholder)
            StubJapaneseTokenizer.ScriptCategory.Hiragana    => "語",     // word
            StubJapaneseTokenizer.ScriptCategory.Katakana    => "名詞",
            StubJapaneseTokenizer.ScriptCategory.Latin       => "外来語", // loanword
            StubJapaneseTokenizer.ScriptCategory.Digit       => "数詞",   // numeral
            StubJapaneseTokenizer.ScriptCategory.Punctuation => "記号",   // symbol
            _                                                => "その他", // other
        };
}
