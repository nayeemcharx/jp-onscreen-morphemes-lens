using JPLens.Models;

namespace JPLens.Interfaces;

/// <summary>
/// Controls the transparent word-overlay window that floats above the screen.
/// All methods must be called on the UI thread.
/// </summary>
public interface IOverlayWindow
{
    /// <summary>
    /// Replaces any current overlays with <paramref name="overlays"/> and makes
    /// the window visible. The window covers the primary monitor.
    /// </summary>
    void ShowOverlays(IReadOnlyList<WordOverlay> overlays);

    /// <summary>Hides the overlay window without destroying it.</summary>
    void HideOverlay();

    /// <summary>True when the overlay window is currently visible.</summary>
    bool IsVisible { get; }
}
