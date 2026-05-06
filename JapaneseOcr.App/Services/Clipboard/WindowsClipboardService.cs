using System.Windows;
using JapaneseOcr.Interfaces;
using WpfApplication = System.Windows.Application;
using WpfClipboard   = System.Windows.Clipboard;

namespace JapaneseOcr.Services;

/// <summary>
/// Writes text to the Windows system clipboard using WPF's
/// <see cref="Clipboard"/> API, which handles thread-safety and retry
/// internally (retries on <c>CLIPBRD_E_CANT_OPEN</c>).
/// </summary>
public sealed class WindowsClipboardService : IClipboardService
{
    /// <inheritdoc/>
    public void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        // Clipboard must be called on the STA thread; WPF's Dispatcher ensures this.
        WpfApplication.Current.Dispatcher.Invoke(() =>
            WpfClipboard.SetDataObject(text, copy: true));
    }
}
