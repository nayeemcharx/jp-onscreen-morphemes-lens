namespace JPLens.Models;

/// <summary>
/// Result returned by <see cref="Interfaces.ILookupService"/>.
/// </summary>
/// <param name="Hiragana">Hiragana reading of the word.</param>
/// <param name="Meaning">Concise English meaning.</param>
public sealed record LookupResult(string Hiragana, string Meaning);
