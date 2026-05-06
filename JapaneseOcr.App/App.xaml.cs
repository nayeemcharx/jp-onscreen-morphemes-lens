using System.IO;
using System.Windows;
using System.Windows.Interop;
using JapaneseOcr.Interfaces;
using JapaneseOcr.Models;
using JapaneseOcr.Overlay;
using JapaneseOcr.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace JapaneseOcr;

/// <summary>
/// Application entry point.
///
/// Startup sequence:
///   1. Configure Serilog (file + debug).
///   2. Build the DI container.
///   3. Load settings.
///   4. Create the helper window (HWND source for hotkeys).
///   5. Register hotkeys.
///   6. Start tray icon.
///   7. Enter WPF message loop.
///
/// DPI awareness is configured via app.manifest (PerMonitorV2).
/// </summary>
public partial class App : System.Windows.Application
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetConsoleOutputCP(uint wCodePageID);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetConsoleCP(uint wCodePageID);

    private ServiceProvider?     _services;
    private Win32HotkeyService?  _hotkeyService;
    private TrayIconService?     _trayService;
    private Window?              _helperWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

#if DEBUG
        // Attach a console window so Serilog's Console sink is visible when
        // running with `dotnet run` or from a terminal in debug builds.
        AllocConsole();
        // Switch both the Win32 console host and .NET streams to UTF-8 (code page 65001)
        // so Japanese characters (and other Unicode) render correctly.
        SetConsoleOutputCP(65001);
        SetConsoleCP(65001);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding  = System.Text.Encoding.UTF8;
#endif

        // ── 1. Logging ────────────────────────────────────────────────────────
        var logDir  = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JapaneseOcr", "Logs");

        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                path:            Path.Combine(logDir, "japaneseocr-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                restrictedToMinimumLevel: LogEventLevel.Information)
            .WriteTo.Debug()
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // ── 2. DI container ───────────────────────────────────────────────────
        var services = new ServiceCollection();
        ConfigureServices(services);
        _services = services.BuildServiceProvider();

        // ── 3. Load settings ──────────────────────────────────────────────────
        // AppSettings is registered as a singleton; the SettingsService loads
        // from disk and the instance is shared across all dependents.

        // ── 4. Create helper window (must be on UI thread) ───────────────────
        _helperWindow = new HotkeyHelperWindow();
        _helperWindow.Show();
        _helperWindow.Hide();

        var helper = new WindowInteropHelper(_helperWindow);
        helper.EnsureHandle();

        // ── 5. Register hotkeys ───────────────────────────────────────────────
        _hotkeyService = _services.GetRequiredService<Win32HotkeyService>();
        _hotkeyService.Initialize(helper.Handle);

        var controller = _services.GetRequiredService<AppController>();
        var settings   = _services.GetRequiredService<AppSettings>();
        var overlay    = _services.GetRequiredService<IOverlayWindow>();

        _hotkeyService.RegisterHotkey(settings.Hotkey,
            () => _ = controller.ToggleAsync());

        // ── 6. Tray icon ──────────────────────────────────────────────────────
        _trayService = _services.GetRequiredService<TrayIconService>();
        _trayService.OnRunOcr = () => _ = controller.ToggleAsync();
        _trayService.Start();

        Log.Information("JapaneseOcr started. Hotkey: {H}", settings.Hotkey);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeyService?.UnregisterAll();
        _hotkeyService?.Dispose();
        _trayService?.Dispose();
        _services?.Dispose();

        Log.Information("JapaneseOcr exiting");
        Log.CloseAndFlush();

        base.OnExit(e);
    }

    // ── DI registration ───────────────────────────────────────────────────────

    private static void ConfigureServices(IServiceCollection services)
    {
        // Logging bridge: Microsoft.Extensions → Serilog
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: false);
        });

        // Settings — load once, share everywhere
        services.AddSingleton<SettingsService>();
        services.AddSingleton<AppSettings>(sp =>
            sp.GetRequiredService<SettingsService>().Load());

        // Screen capture
        services.AddSingleton<IScreenCaptureService, WindowsScreenCaptureService>();

        // OCR (Windows.Media.Ocr)
        // To swap: replace WindowsOcrService with your implementation
        services.AddSingleton<IOcrService, WindowsOcrService>();

        // Japanese tokenizer — MeCab via MeCab.DotNet (IPAdic bundled, no external server needed).
        // To use the Sudachi HTTP server instead: replace MeCabTokenizer with SudachiHttpTokenizer.
        // To use the built-in stub (no analysis): replace with StubJapaneseTokenizer.
        services.AddSingleton<IJapaneseTokenizer, MeCabTokenizer>();

        // Token→box mapping
        services.AddSingleton<ITokenBoxMapper, TokenBoxMapper>();

        // Clipboard
        services.AddSingleton<IClipboardService, WindowsClipboardService>();

        // LLM lookup
        // To use the local LLM server instead: replace GoogleTranslateLookupService with LlmLookupService.
        services.AddSingleton<ILookupService, GoogleTranslateLookupService>();

        // Hotkey service
        services.AddSingleton<Win32HotkeyService>();
        services.AddSingleton<IHotkeyService>(sp => sp.GetRequiredService<Win32HotkeyService>());

        // Overlay window — must be created on UI thread; registered as singleton
        services.AddSingleton<OverlayWindow>(sp => new OverlayWindow(
            sp.GetRequiredService<ILookupService>(),
            sp.GetRequiredService<AppSettings>()));
        services.AddSingleton<IOverlayWindow>(sp =>
            sp.GetRequiredService<OverlayWindow>());

        // Tray icon service
        services.AddSingleton<TrayIconService>();

        // App controller
        services.AddSingleton<AppController>();
    }
}

/// <summary>
/// A minimal hidden WPF window used solely to provide a stable HWND for
/// Win32 RegisterHotKey. It is never shown to the user.
/// </summary>
file sealed class HotkeyHelperWindow : Window
{
    public HotkeyHelperWindow()
    {
        Width           = 0;
        Height          = 0;
        WindowStyle     = WindowStyle.None;
        ShowInTaskbar   = false;
        Visibility      = Visibility.Hidden;
        AllowsTransparency = true;
        Background      = System.Windows.Media.Brushes.Transparent;
    }
}
