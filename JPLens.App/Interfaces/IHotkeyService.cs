namespace JPLens.Interfaces;

/// <summary>
/// Registers and manages process-wide global hotkeys.
/// The implementation must be initialized with a window handle before
/// <see cref="RegisterHotkey"/> is called.
/// </summary>
public interface IHotkeyService : IDisposable
{
    /// <summary>
    /// Provides the HWND used for <c>RegisterHotKey</c> Win32 calls.
    /// Must be called once during application startup, before any
    /// <see cref="RegisterHotkey"/> calls.
    /// </summary>
    void Initialize(IntPtr windowHandle);

    /// <summary>
    /// Registers a global hotkey. Format: "CTRL+SHIFT+J".
    /// Supported modifiers: CTRL, SHIFT, ALT, WIN.
    /// </summary>
    /// <param name="hotkey">Case-insensitive hotkey string.</param>
    /// <param name="callback">Invoked on the UI thread when the hotkey fires.</param>
    void RegisterHotkey(string hotkey, Action callback);

    /// <summary>Unregisters all hotkeys registered through this service.</summary>
    void UnregisterAll();
}
