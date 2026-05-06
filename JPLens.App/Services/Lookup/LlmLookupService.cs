using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using JPLens.Interfaces;
using JPLens.Models;
using Microsoft.Extensions.Logging;

namespace JPLens.Services;

/// <summary>
/// Calls the local LLM lookup server (llm-server/) to retrieve the hiragana
/// reading and English meaning of a Japanese word.
/// Cache logic lives on the server side; this service is a thin HTTP client.
/// </summary>
public sealed class LlmLookupService : ILookupService, IDisposable
{
    private readonly HttpClient              _http;
    private readonly AppSettings             _settings;
    private readonly ILogger<LlmLookupService> _logger;

    public LlmLookupService(AppSettings settings, ILogger<LlmLookupService> logger)
    {
        _settings = settings;
        _logger   = logger;

        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    public async Task<LookupResult?> LookupAsync(string word, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(word))
            return null;

        try
        {
            var url = $"{_settings.LlmServerUrl.TrimEnd('/')}/lookup";

            using var response = await _http.PostAsJsonAsync(url, new { word }, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "LLM server returned {Status} for word '{Word}': {Body}",
                    (int)response.StatusCode, word, body);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc  = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var hiragana = root.GetProperty("hiragana").GetString() ?? word;
            var meaning  = root.GetProperty("meaning").GetString()  ?? "unknown";

            return new LookupResult(hiragana, meaning);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM lookup failed for word '{Word}'", word);
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
