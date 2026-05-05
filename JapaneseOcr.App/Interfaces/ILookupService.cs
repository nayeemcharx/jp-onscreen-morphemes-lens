using JapaneseOcr.Models;

namespace JapaneseOcr.Interfaces;

/// <summary>
/// Looks up a Japanese word and returns its hiragana reading and English meaning.
/// Implementations call the local LLM server which caches results in SQLite.
/// </summary>
public interface ILookupService
{
    /// <summary>
    /// Looks up <paramref name="word"/>.
    /// Returns <c>null</c> when the server is unreachable or returns an error.
    /// </summary>
    Task<LookupResult?> LookupAsync(string word, CancellationToken ct = default);
}
