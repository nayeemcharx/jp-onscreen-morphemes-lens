using JPLens.Models;

namespace JPLens.Interfaces;

/// <summary>
/// Maps a list of <see cref="JapaneseToken"/>s to screen-space bounding boxes
/// by correlating token character indices with the OCR character boxes.
/// </summary>
public interface ITokenBoxMapper
{
    /// <summary>
    /// Produces a <see cref="WordOverlay"/> for each displayable token in
    /// <paramref name="tokens"/> by locating the corresponding character boxes
    /// within <paramref name="line"/>.
    /// </summary>
    /// <param name="line">The OCR line containing character-level boxes.</param>
    /// <param name="tokens">Tokens derived from <paramref name="line"/>'s text.</param>
    /// <param name="frame">Source frame used to translate monitor coordinates.</param>
    /// <param name="ocrScale">The upscale factor used during OCR preprocessing.</param>
    List<WordOverlay> MapTokensToBoxes(
        OcrLine          line,
        List<JapaneseToken> tokens,
        ScreenFrame      frame,
        double           ocrScale);
}
