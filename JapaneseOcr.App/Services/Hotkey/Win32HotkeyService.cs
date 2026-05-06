using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using JapaneseOcr.Interfaces;
using Microsoft.Extensions.Logging;
using WpfApplication = System.Windows.Application;

namespace JapaneseOcr.Services;

/// <summary>
/// Registers and dispatches process-wide global hotkeys using the Win32
/// <c>RegisterHotKey</c> / <c>UnregisterHotKey</c> API.
///
/// Usage:
///   1. Call Initialize(hwnd) once after the helper window's HWND is available.
///   2. Call RegisterHotkey("CTRL+SHIFT+J", callback).
///   3. Call UnregisterAll() / Dispose() on shutdown.
///
/// Supported modifiers: CTRL, SHIFT, ALT, WIN.
/// </summary>
public sealed class Win32HotkeyService : IHotkeyService
{
    // ── Win32 P/Invoke ────────────────────────────────────────────────────────
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int  WM_HOTKEY   = 0x0312;
    private const uint MOD_ALT     = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT   = 0x0004;
    private const uint MOD_WIN     = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    // ─────────────────────────────────────────────────────────────────────────

    private readonly ILogger<Win32HotkeyService> _logger;

    private IntPtr                    _hwnd;
    private HwndSource?               _source;
    private readonly Dictionary<int, Action> _callbacks = new();
    private int                       _nextId  = 0xC0DE; // arbitrary starting ID
    private bool                      _disposed;

    public Win32HotkeyService(ILogger<Win32HotkeyService> logger)
        => _logger = logger;

    /// <inheritdoc/>
    public void Initialize(IntPtr windowHandle)
    {
        _hwnd   = windowHandle;
        _source = HwndSource.FromHwnd(windowHandle);
        _source?.AddHook(WndProc);
        _logger.LogDebug("HotkeyService initialised with HWND 0x{H:X}", windowHandle);
    }

    /// <inheritdoc/>
    public void RegisterHotkey(string hotkey, Action callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!TryParseHotkey(hotkey, out uint modifiers, out uint vk))
            throw new ArgumentException($"Cannot parse hotkey string: '{hotkey}'", nameof(hotkey));

        int id = _nextId++;
        if (!RegisterHotKey(_hwnd, id, modifiers | MOD_NOREPEAT, vk))
        {
            int err = Marshal.GetLastWin32Error();
            _logger.LogError("RegisterHotKey failed for '{H}' — Win32 error {E}", hotkey, err);
            throw new InvalidOperationException(
                $"Could not register hotkey '{hotkey}'. " +
                "Another application may be using it (Win32 error {err}).");
        }

        _callbacks[id] = callback;
        _logger.LogInformation("Registered hotkey '{H}' (id={I})", hotkey, id);
    }

    /// <inheritdoc/>
    public void UnregisterAll()
    {
        foreach (int id in _callbacks.Keys)
            UnregisterHotKey(_hwnd, id);

        _callbacks.Clear();
        _logger.LogDebug("All hotkeys unregistered");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAll();
        _source?.RemoveHook(WndProc);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ──────────────────────────────────────────────────────────────────────────

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_callbacks.TryGetValue(id, out var cb))
            {
                handled = true;
                // Dispatch on the WPF UI thread
                WpfApplication.Current?.Dispatcher.InvokeAsync(cb);
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Parses "CTRL+SHIFT+J" → (modifiers, virtualKey).
    /// Keys are mapped by name to Virtual-Key codes.
    /// </summary>
    private static bool TryParseHotkey(string hotkey, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk        = 0;

        if (string.IsNullOrWhiteSpace(hotkey))
            return false;

        var parts = hotkey.ToUpperInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries);

        string? keyPart = null;

        foreach (var part in parts)
        {
            switch (part.Trim())
            {
                case "CTRL":    modifiers |= MOD_CONTROL; break;
                case "CONTROL": modifiers |= MOD_CONTROL; break;
                case "SHIFT":   modifiers |= MOD_SHIFT;   break;
                case "ALT":     modifiers |= MOD_ALT;     break;
                case "WIN":     modifiers |= MOD_WIN;     break;
                default:        keyPart = part.Trim();    break;
            }
        }

        if (keyPart is null)
            return false;

        vk = KeyNameToVK(keyPart);
        return vk != 0;
    }

    private static uint KeyNameToVK(string name) => name switch
    {
        // Letters A–Z → VK_A(0x41) … VK_Z(0x5A)
        { Length: 1 } when name[0] >= 'A' && name[0] <= 'Z' => (uint)(name[0] - 'A' + 0x41),

        // Digits 0–9 → VK_0(0x30) … VK_9(0x39)
        { Length: 1 } when name[0] >= '0' && name[0] <= '9' => (uint)(name[0] - '0' + 0x30),

        // Function keys F1–F12
        "F1"  => 0x70, "F2"  => 0x71, "F3"  => 0x72, "F4"  => 0x73,
        "F5"  => 0x74, "F6"  => 0x75, "F7"  => 0x76, "F8"  => 0x77,
        "F9"  => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,

        "SPACE"     => 0x20,
        "RETURN"    => 0x0D,
        "ESCAPE"    => 0x1B,
        "ESC"       => 0x1B,
        "TAB"       => 0x09,
        "BACK"      => 0x08,
        "DELETE"    => 0x2E,
        "INSERT"    => 0x2D,
        "HOME"      => 0x24,
        "END"       => 0x23,
        "PAGEUP"    => 0x21,
        "PAGEDOWN"  => 0x22,
        "LEFT"      => 0x25,
        "UP"        => 0x26,
        "RIGHT"     => 0x27,
        "DOWN"      => 0x28,

        _ => 0,
    };
}
