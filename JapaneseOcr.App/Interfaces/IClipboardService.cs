namespace JapaneseOcr.Interfaces;

/// <summary>
/// Writes text to the system clipboard.
/// Abstracted for testability; the real implementation calls Win32/WPF APIs.
/// </summary>
public interface IClipboardService
{
    /// <summary>
    /// Sets the clipboard text to <paramref name="text"/>.
    /// No-ops silently when <paramref name="text"/> is null or empty.
    /// </summary>
    void CopyText(string text);
}
