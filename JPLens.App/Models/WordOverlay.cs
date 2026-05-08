namespace JPLens.Models;

/// <summary>
/// A displayable overlay box representing one tokenized Japanese word.
/// <see cref="ScreenBoundingBox"/> is in absolute physical screen pixel
/// coordinates (i.e. includes the monitor's origin offset and is independent
/// of WPF DPI scaling).
/// </summary>
public sealed class WordOverlay
{
    /// <summary>The surface form of the word (as seen on screen).</summary>
    public string    SurfaceText        { get; init; } = string.Empty;

    /// <summary>Dictionary/base form, if available.</summary>
    public string    DictionaryForm     { get; init; } = string.Empty;

    /// <summary>Part-of-speech tag in Japanese.</summary>
    public string    PartOfSpeech       { get; init; } = string.Empty;

    /// <summary>
    /// Hiragana reading of the word as provided by the tokenizer (e.g. MeCab IPAdic).
    /// Empty when the tokenizer does not supply reading information.
    /// </summary>
    public string    Reading            { get; init; } = string.Empty;

    /// <summary>
    /// Bounding box in absolute physical screen pixel coordinates.
    /// (0,0) is the top-left of the primary monitor for standard setups.
    /// </summary>
    public PixelRect ScreenBoundingBox  { get; init; }

    /// <summary>Full text of the OCR line this token came from.</summary>
    public string    SourceLineText     { get; init; } = string.Empty;

    /// <summary>OCR confidence for the originating line (0–1).</summary>
    public double    Confidence         { get; init; }

    /// <summary>Character start index of this token within <see cref="SourceLineText"/>.</summary>
    public int       StartCharIndex     { get; init; }

    /// <summary>Character end index (exclusive) of this token within <see cref="SourceLineText"/>.</summary>
    public int       EndCharIndex       { get; init; }

    /// <summary>
    /// Returns the text that should be copied to the clipboard, based on the
    /// current settings flag.
    /// </summary>
    public string GetCopyText(bool preferDictionaryForm)
        => preferDictionaryForm && !string.IsNullOrWhiteSpace(DictionaryForm)
            ? DictionaryForm
            : SurfaceText;
}
