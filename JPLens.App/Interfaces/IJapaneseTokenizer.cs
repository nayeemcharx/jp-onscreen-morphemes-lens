using JPLens.Models;

namespace JPLens.Interfaces;

/// <summary>
/// Splits a Japanese string into morphemes/tokens.
/// Implementations can use:
///   - <see cref="Services.StubJapaneseTokenizer"/>  (built-in, no install)
///   - MeCab via subprocess (brew/apt/chocolatey install mecab)
///   - Sudachi via subprocess or JVM bridge
///   - Kuromoji-compatible HTTP service
/// The returned <see cref="JapaneseToken.StartCharIndex"/> and
/// <see cref="JapaneseToken.EndCharIndex"/> must index into the same normalized
/// string that <see cref="Processing.TextNormalizer.Normalize"/> produces.
/// </summary>
public interface IJapaneseTokenizer
{
    /// <summary>
    /// Tokenizes <paramref name="text"/> and returns an ordered list of tokens.
    /// The method must be thread-safe.
    /// </summary>
    List<JapaneseToken> Tokenize(string text);
}
