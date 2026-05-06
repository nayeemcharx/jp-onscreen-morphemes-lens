using System.Text;

namespace JPLens.Processing;

/// <summary>
/// Normalizes Japanese text for consistent tokenization and index mapping.
/// Both the OCR output and the tokenizer input must be normalized with the
/// same method so that character indices from the tokenizer align with
/// character positions in the OCR line text.
/// </summary>
public static class TextNormalizer
{
    /// <summary>
    /// Normalizes a Japanese string:
    /// - Converts full-width ASCII to half-width (e.g. Ａ→A, １→1)
    /// - Converts half-width katakana to full-width
    /// - Collapses multiple whitespace characters to a single space
    /// - Trims leading/trailing whitespace
    /// </summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        // Unicode NFC normalization ensures composed characters
        var normalized = text.Normalize(NormalizationForm.FormC);

        var sb = new StringBuilder(normalized.Length);
        bool lastWasSpace = false;

        foreach (char c in normalized)
        {
            char converted = ConvertCharacter(c);

            if (converted == ' ')
            {
                if (!lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                sb.Append(converted);
                lastWasSpace = false;
            }
        }

        // Trim trailing space
        if (sb.Length > 0 && sb[^1] == ' ')
            sb.Length--;

        return sb.ToString();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Internal helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static char ConvertCharacter(char c)
    {
        // Full-width ASCII (！ U+FF01 … ～ U+FF5E) → half-width
        if (c >= '\uFF01' && c <= '\uFF5E')
            return (char)(c - 0xFF01 + 0x21);

        // Full-width space (　 U+3000) → half-width space
        if (c == '\u3000')
            return ' ';

        // Half-width katakana → full-width katakana
        // Covers: ｦ (U+FF66) … ﾟ (U+FF9F)
        if (c >= '\uFF66' && c <= '\uFF9F')
            return HalfKatakanaToFull(c);

        // Whitespace (CR, LF, tab) → space
        if (c is '\r' or '\n' or '\t')
            return ' ';

        return c;
    }

    /// <summary>
    /// Maps a half-width katakana character to its full-width equivalent.
    /// The mapping follows JIS X 0208 / Unicode tables.
    /// </summary>
    private static char HalfKatakanaToFull(char c)
    {
        // Simple codepoint mapping (does not handle voiced/semi-voiced
        // combining marks—those are rare in OCR output and handled
        // by NFC normalization above when possible).
        return c switch
        {
            '\uFF66' => 'ヲ',
            '\uFF67' => 'ァ',
            '\uFF68' => 'ィ',
            '\uFF69' => 'ゥ',
            '\uFF6A' => 'ェ',
            '\uFF6B' => 'ォ',
            '\uFF6C' => 'ッ',
            '\uFF6D' => 'ュ',
            '\uFF6E' => 'ョ',
            '\uFF6F' => 'ッ',
            '\uFF70' => 'ー',
            '\uFF71' => 'ア',
            '\uFF72' => 'イ',
            '\uFF73' => 'ウ',
            '\uFF74' => 'エ',
            '\uFF75' => 'オ',
            '\uFF76' => 'カ',
            '\uFF77' => 'キ',
            '\uFF78' => 'ク',
            '\uFF79' => 'ケ',
            '\uFF7A' => 'コ',
            '\uFF7B' => 'サ',
            '\uFF7C' => 'シ',
            '\uFF7D' => 'ス',
            '\uFF7E' => 'セ',
            '\uFF7F' => 'ソ',
            '\uFF80' => 'タ',
            '\uFF81' => 'チ',
            '\uFF82' => 'ツ',
            '\uFF83' => 'テ',
            '\uFF84' => 'ト',
            '\uFF85' => 'ナ',
            '\uFF86' => 'ニ',
            '\uFF87' => 'ヌ',
            '\uFF88' => 'ネ',
            '\uFF89' => 'ノ',
            '\uFF8A' => 'ハ',
            '\uFF8B' => 'ヒ',
            '\uFF8C' => 'フ',
            '\uFF8D' => 'ヘ',
            '\uFF8E' => 'ホ',
            '\uFF8F' => 'マ',
            '\uFF90' => 'ミ',
            '\uFF91' => 'ム',
            '\uFF92' => 'メ',
            '\uFF93' => 'モ',
            '\uFF94' => 'ヤ',
            '\uFF95' => 'ユ',
            '\uFF96' => 'ヨ',
            '\uFF97' => 'ラ',
            '\uFF98' => 'リ',
            '\uFF99' => 'ル',
            '\uFF9A' => 'レ',
            '\uFF9B' => 'ロ',
            '\uFF9C' => 'ワ',
            '\uFF9D' => 'ン',
            '\uFF9E' => '゛',
            '\uFF9F' => '゜',
            _        => c,
        };
    }
}
