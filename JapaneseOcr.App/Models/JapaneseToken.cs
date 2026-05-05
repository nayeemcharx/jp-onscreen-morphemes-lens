namespace JapaneseOcr.Models;

/// <summary>
/// A morpheme/word produced by a Japanese tokenizer.
/// <see cref="StartCharIndex"/> and <see cref="EndCharIndex"/> are character
/// indices (inclusive start, exclusive end) into the normalized OCR line text
/// from which the token was extracted.
/// </summary>
public sealed class JapaneseToken
{
    /// <summary>The surface form as it appears in the OCR text.</summary>
    public string SurfaceText      { get; init; } = string.Empty;

    /// <summary>
    /// Dictionary/base form (e.g. "食べる" for "食べた").
    /// Falls back to <see cref="SurfaceText"/> when unavailable.
    /// </summary>
    public string DictionaryForm   { get; init; } = string.Empty;

    /// <summary>
    /// Part-of-speech tag in Japanese (e.g. "名詞", "動詞", "助詞").
    /// Empty when the tokenizer does not provide POS information.
    /// </summary>
    public string PartOfSpeech     { get; init; } = string.Empty;

    /// <summary>Inclusive start index into the normalized line text.</summary>
    public int    StartCharIndex   { get; init; }

    /// <summary>Exclusive end index into the normalized line text.</summary>
    public int    EndCharIndex     { get; init; }
}
