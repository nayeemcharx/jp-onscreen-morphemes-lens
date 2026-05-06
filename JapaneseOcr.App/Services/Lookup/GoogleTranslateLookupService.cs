using System.Net.Http;
using System.Text.Json;
using System.Web;
using JapaneseOcr.Interfaces;
using JapaneseOcr.Models;
using Microsoft.Extensions.Logging;

namespace JapaneseOcr.Services;

/// <summary>
/// Looks up the English meaning of a Japanese word by scraping Google Translate's
/// public (unofficial) JSON endpoint — no API key required.
///
/// Endpoint:
///   GET https://translate.googleapis.com/translate_a/single
///       ?client=gtx&amp;sl=ja&amp;tl=en&amp;dt=t&amp;q={encodedWord}
///
/// Response shape (jagged JSON array):
///   [ [ ["translated segment", "original segment", ...], ... ], null, "ja", ... ]
///
/// The full translation is assembled by concatenating all first-element entries
/// in the outer translation array (index [0][n][0]).
///
/// NOTE: This endpoint is unofficial and undocumented; Google may change or
/// throttle it at any time.  The service returns <c>null</c> (graceful
/// degradation) on any network or parsing error.
///
/// The <see cref="LookupResult.Hiragana"/> field is left empty because the
/// reading is supplied directly from the MeCab tokenizer via
/// <see cref="WordOverlay.Reading"/>.
/// </summary>
public sealed class GoogleTranslateLookupService : ILookupService, IDisposable
{
    private const string BaseUrl =
        "https://translate.googleapis.com/translate_a/single";

    private readonly HttpClient                              _http;
    private readonly ILogger<GoogleTranslateLookupService>  _logger;

    public GoogleTranslateLookupService(ILogger<GoogleTranslateLookupService> logger)
    {
        _logger = logger;

        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        // Identify ourselves as a standard browser so the endpoint doesn't
        // block requests outright.  gtx client does not require this but it
        // reduces the chance of being rate-limited in practice.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/124.0.0.0 Safari/537.36");
    }

    /// <inheritdoc/>
    public async Task<LookupResult?> LookupAsync(string word, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(word))
            return null;

        try
        {
            var url = $"{BaseUrl}?client=gtx&sl=ja&tl=en&dt=t&q={HttpUtility.UrlEncode(word)}";

            using var response = await _http.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Google Translate returned {Status} for word '{Word}'",
                    (int)response.StatusCode, word);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var meaning = ParseTranslation(json, word);

            if (meaning is null)
                return null;

            // Reading (Hiragana) is intentionally left empty — it is provided
            // by the MeCab tokenizer and stored on WordOverlay.Reading.
            return new LookupResult(Hiragana: string.Empty, Meaning: meaning);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google Translate lookup failed for word '{Word}'", word);
            return null;
        }
    }

    /// <summary>
    /// Parses the jagged JSON array returned by the translate endpoint and
    /// concatenates all translated text segments.
    ///
    /// Structure: <c>[ [ ["text", "orig", ...], ... ], null, "ja", ... ]</c>
    /// </summary>
    private string? ParseTranslation(string json, string originalWord)
    {
        try
        {
            using var doc  = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // root[0] = array of translation segments
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                return null;

            var segments = root[0];
            if (segments.ValueKind != JsonValueKind.Array)
                return null;

            var parts = new System.Text.StringBuilder();

            foreach (var segment in segments.EnumerateArray())
            {
                // Each segment is [translated_text, original_text, ...]
                if (segment.ValueKind == JsonValueKind.Array &&
                    segment.GetArrayLength() > 0 &&
                    segment[0].ValueKind == JsonValueKind.String)
                {
                    parts.Append(segment[0].GetString());
                }
            }

            var result = parts.ToString().Trim();
            return string.IsNullOrEmpty(result) ? null : result;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Failed to parse Google Translate response for word '{Word}'", originalWord);
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
