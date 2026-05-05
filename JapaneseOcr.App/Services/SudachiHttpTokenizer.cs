using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using JapaneseOcr.Interfaces;
using JapaneseOcr.Models;
using Microsoft.Extensions.Logging;

namespace JapaneseOcr.Services;

/// <summary>
/// Japanese tokenizer that delegates to the local SudachiPy FastAPI server
/// (see tokenizer-server/main.py).
///
/// The HTTP call is made synchronously via GetAwaiter().GetResult() because
/// <see cref="IJapaneseTokenizer.Tokenize"/> is called from inside
/// <c>Task.Run</c> in <see cref="AppController"/>, so blocking a thread-pool
/// thread is safe.
///
/// If the server is unreachable or returns an error the method logs a warning
/// and returns an empty list, which produces no overlay boxes for that line
/// (graceful degradation).
///
/// Setup:
///   cd tokenizer-server
///   pip install -r requirements.txt
///   uvicorn main:app --host 0.0.0.0 --port 8000
/// </summary>
public sealed class SudachiHttpTokenizer : IJapaneseTokenizer
{
    private readonly HttpClient                         _http;
    private readonly ILogger<SudachiHttpTokenizer>      _logger;

    public SudachiHttpTokenizer(AppSettings settings, ILogger<SudachiHttpTokenizer> logger)
    {
        _logger = logger;
        _http   = new HttpClient
        {
            BaseAddress = new Uri(settings.SudachiServerUrl.TrimEnd('/')),
            Timeout     = TimeSpan.FromSeconds(5),
        };
    }

    /// <inheritdoc/>
    public List<JapaneseToken> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        try
        {
            var request  = new TokenizeRequest { Text = text };
            var response = _http
                .PostAsJsonAsync("/tokenize", request)
                .GetAwaiter().GetResult();

            response.EnsureSuccessStatusCode();

            var result = response.Content
                .ReadFromJsonAsync<TokenizeResponse>()
                .GetAwaiter().GetResult();

            if (result is null)
                return [];

            return result.Tokens
                .Select(t => new JapaneseToken
                {
                    SurfaceText    = t.Surface,
                    DictionaryForm = t.DictionaryForm,
                    // Combine main POS and sub-category so filters can match
                    // either level, e.g. "助詞" or "助詞-格助詞".
                    PartOfSpeech   = string.IsNullOrEmpty(t.PartOfSpeechDetail)
                        ? t.PartOfSpeech
                        : $"{t.PartOfSpeech}-{t.PartOfSpeechDetail}",
                    StartCharIndex = t.Start,
                    EndCharIndex   = t.End,
                })
                .ToList();
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning(
                "Sudachi server timed out for input (len={L}). Is the server running?",
                text.Length);
            return [];
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "Sudachi server unreachable. Is it running at {U}?",
                _http.BaseAddress);
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sudachi tokenization failed for input (len={L})", text.Length);
            return [];
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // JSON DTO types (private — not part of the public API)
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class TokenizeRequest
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;
    }

    private sealed class TokenizeResponse
    {
        [JsonPropertyName("tokens")]
        public List<TokenDto> Tokens { get; set; } = [];
    }

    private sealed class TokenDto
    {
        [JsonPropertyName("surface")]
        public string Surface { get; set; } = string.Empty;

        [JsonPropertyName("dictionary_form")]
        public string DictionaryForm { get; set; } = string.Empty;

        [JsonPropertyName("reading")]
        public string Reading { get; set; } = string.Empty;

        [JsonPropertyName("part_of_speech")]
        public string PartOfSpeech { get; set; } = string.Empty;

        [JsonPropertyName("part_of_speech_detail")]
        public string PartOfSpeechDetail { get; set; } = string.Empty;

        [JsonPropertyName("start")]
        public int Start { get; set; }

        [JsonPropertyName("end")]
        public int End { get; set; }
    }
}
