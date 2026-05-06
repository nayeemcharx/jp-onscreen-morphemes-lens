using System.Text;
using JapaneseOcr.Interfaces;
using JapaneseOcr.Models;
using MeCab;
using Microsoft.Extensions.Logging;

namespace JapaneseOcr.Services;

/// <summary>
/// Japanese tokenizer backed by MeCab.DotNet (IPAdic dictionary bundled).
///
/// No external process or server is required — the dictionary ships inside the
/// NuGet package and is resolved automatically at build time via the
/// <c>MeCabUseDefaultDictionary</c> MSBuild property (default: true).
///
/// IPAdic feature layout (comma-separated, <see cref="MeCabNode.Feature"/>):
///   [0] 品詞            part-of-speech (e.g. 名詞, 動詞)
///   [1] 品詞細分類1      POS sub-category 1
///   [2] 品詞細分類2      POS sub-category 2
///   [3] 品詞細分類3      POS sub-category 3
///   [4] 活用型           conjugation type
///   [5] 活用形           conjugation form
///   [6] 原形             dictionary / base form
///   [7] 読み             reading in katakana  → converted to hiragana
///   [8] 発音             pronunciation in katakana
///
/// Thread-safety: <see cref="MeCabTagger"/> is not documented as thread-safe,
/// so all calls are serialised with a lightweight lock.  Tokenization is
/// called from inside <c>Task.Run</c> in <see cref="AppController"/> and the
/// lock contention is negligible in practice.
/// </summary>
public sealed class MeCabTokenizer : IJapaneseTokenizer, IDisposable
{
    private readonly ILogger<MeCabTokenizer> _logger;
    private readonly MeCabTagger             _tagger;
    private readonly object                  _lock = new();

    public MeCabTokenizer(ILogger<MeCabTokenizer> logger)
    {
        _logger = logger;
        _tagger = MeCabTagger.Create(new MeCabParam());
    }

    /// <inheritdoc/>
    public List<JapaneseToken> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var tokens = new List<JapaneseToken>();

        try
        {
            IEnumerable<MeCabNode> nodes;

            lock (_lock)
                nodes = _tagger.ParseToNodes(text).ToList();

            int charIndex = 0;

            foreach (var node in nodes)
            {
                // CharType == 0 means BOS/EOS boundary nodes — skip them.
                if (node.CharType == 0)
                    continue;

                var surface = node.Surface;
                int start   = charIndex;
                int end     = charIndex + surface.Length;
                charIndex   = end;

                var features = node.Feature.Split(',');

                var pos       = features.Length > 0 ? features[0] : string.Empty;
                var posDetail = features.Length > 1 && features[1] != "*" ? features[1] : string.Empty;
                var dictForm  = features.Length > 6 && features[6] != "*" ? features[6] : surface;
                var reading   = features.Length > 7 && features[7] != "*"
                    ? KatakanaToHiragana(features[7])
                    : string.Empty;

                tokens.Add(new JapaneseToken
                {
                    SurfaceText    = surface,
                    DictionaryForm = dictForm,
                    PartOfSpeech   = string.IsNullOrEmpty(posDetail)
                        ? pos
                        : $"{pos}-{posDetail}",
                    Reading        = reading,
                    StartCharIndex = start,
                    EndCharIndex   = end,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MeCab tokenization failed for text of length {Len}", text.Length);
        }

        return tokens;
    }

    /// <summary>
    /// Converts a katakana string to hiragana.
    /// Katakana U+30A1–U+30F6 maps to hiragana U+3041–U+3096 by subtracting 0x60.
    /// Characters outside that range (e.g. ー, symbols) are left as-is.
    /// </summary>
    private static string KatakanaToHiragana(string katakana)
    {
        var sb = new StringBuilder(katakana.Length);
        foreach (var c in katakana)
            sb.Append(c is >= '\u30A1' and <= '\u30F6' ? (char)(c - 0x60) : c);
        return sb.ToString();
    }

    public void Dispose() => _tagger.Dispose();
}
