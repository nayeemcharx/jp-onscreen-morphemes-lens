# JPLens

A lightweight Windows desktop app that captures your screen on demand, detects Japanese text, and renders a transparent clickable overlay. Click any word box to copy it to your clipboard instantly.

![Demo](images/demo.gif)

---

## Quick Start

1. Go to the [Releases](../../releases) page and download the latest `JPLens.zip`.
2. Extract the zip anywhere — no installer needed.
3. Run `JPLens.exe`.
4. A tray icon appears in the system tray to confirm the app is running.

**That's it.** The app runs silently in the background until you need it.

---

## Usage

| Action | Result |
|---|---|
| `Ctrl+Shift+J` | Scan the screen and show the word overlay |
| `Ctrl+Shift+J` again | Dismiss the overlay |
| Click a word box | Copy the word to your clipboard |
| `Esc` | Dismiss the overlay |
| Right-click tray icon | Exit the application |

---

## Requirements

- **Windows 10 1803** or later (build 17134+)
- **Japanese OCR language pack** installed

### Install the Japanese language pack

**Option A — Windows Settings:**
```
Settings → Time & language → Language & region → Add a language → 日本語
Ensure the "OCR" optional feature is checked.
```

**Option B — Command line (elevated):**
```cmd
dism /Online /Add-Capability /CapabilityName:Language.OCR~~~ja-JP~0.0.1.0
```

---

## Requirements

| Requirement | Notes |
|---|---|
| Windows 10 1803+ (build 17134) | For `Windows.Media.Ocr` |
| .NET 8 SDK | [Download](https://dotnet.microsoft.com/download/dotnet/8.0) |
| Japanese language pack | See install instructions below |
| Visual Studio 2022 17.x or `dotnet CLI` | |

### Install the Japanese OCR language pack

Option A — Windows Settings:

```
Settings → Time & language → Language & region → Add a language → 日本語
Then ensure "Handwriting" and "OCR" optional features are installed.
```

Option B — DISM (elevated command prompt):

```cmd
dism /Online /Add-Capability /CapabilityName:Language.OCR~~~ja-JP~0.0.1.0
```

Verify by running this PowerShell snippet:

```powershell
[Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]
$langs = [Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages
$langs | Select-Object DisplayName
```

You should see `日本語` in the list.

---

## Project Structure

```
JPLens.sln
├── JPLens.App/
│   ├── App.xaml / App.xaml.cs          — Application entry point, DI setup
│   ├── app.manifest                    — PerMonitorV2 DPI, Windows 10/11 compat
│   ├── AppController.cs                — Pipeline orchestrator
│   │
│   ├── Models/
│   │   ├── PixelRect.cs                — Physical-pixel rectangle
│   │   ├── ScreenFrame.cs              — Captured monitor frame
│   │   ├── OcrModels.cs                — OcrCharacter, OcrLine, OcrResult
│   │   ├── JapaneseToken.cs            — Tokenizer output
│   │   ├── WordOverlay.cs              — Display overlay item
│   │   └── AppSettings.cs             — User settings model
│   │
│   ├── Interfaces/
│   │   ├── IScreenCaptureService.cs
│   │   ├── IOcrService.cs
│   │   ├── IJapaneseTokenizer.cs
│   │   ├── ITokenBoxMapper.cs
│   │   ├── IOverlayWindow.cs
│   │   ├── IClipboardService.cs
│   │   └── IHotkeyService.cs
│   │
│   ├── Services/
│   │   ├── MonitorService.cs           — Win32 monitor + DPI info
│   │   ├── WindowsScreenCaptureService.cs  — GDI BitBlt capture
│   │   ├── WindowsOcrService.cs        — Windows.Media.Ocr
│   │   ├── StubJapaneseTokenizer.cs    — Script-group tokenizer (MVP)
│   │   ├── TokenBoxMapper.cs           — Maps tokens → screen boxes
│   │   ├── WindowsClipboardService.cs  — WPF clipboard
│   │   ├── Win32HotkeyService.cs       — RegisterHotKey wrapper
│   │   ├── TrayIconService.cs          — System tray (Hardcodet)
│   │   └── SettingsService.cs         — JSON settings load/save
│   │
│   ├── Processing/
│   │   ├── TextNormalizer.cs           — Full-width→half-width, NFC, etc.
│   │   ├── GeometryHelper.cs           — Union, IoU, Scale, Translate
│   │   ├── ImagePreprocessor.cs        — Scale + contrast + sharpen
│   │   ├── CharacterBoxEstimator.cs    — Fallback char box estimation
│   │   └── OverlayPostProcessor.cs    — Dedup + tiny box removal
│   │
│   └── Overlay/
│       ├── OverlayWindow.xaml          — Borderless transparent WPF window
│       └── OverlayWindow.xaml.cs      — Rendering + WM_NCHITTEST click-through
│
└── JPLens.Tests/
    ├── TokenizerIndexMappingTests.cs
    ├── TokenFilteringTests.cs
    ├── RectangleUnionTests.cs
    ├── TokenBoxEstimationTests.cs
    └── DuplicateOverlayRemovalTests.cs
```

---

## Building

```bash
# Restore & build
dotnet restore
dotnet build JPLens.sln

# Run
dotnet run --project JPLens.App

# Run tests
dotnet test JPLens.Tests
```

Or open `JPLens.sln` in Visual Studio 2022 and press F5.

---

## Usage

1. Launch `JPLens.exe` — a tray icon appears.
2. Open any application with Japanese text (game, browser, video player, etc.).
3. Press **Ctrl+Shift+J**.
4. Gold overlay boxes appear over each detected word.
5. **Click** a box to copy the word to the clipboard.
   A small "Copied: ..." popup appears briefly.
6. Press **ESC** or click outside all boxes to hide the overlay.

---

## Configuration

Settings are stored at `%AppData%\JPLens\settings.json`. Edit manually or
wait for the settings UI in Phase 7.

```json
{
  "MinimumOcrConfidence": 0.60,
  "OcrScale": 2.0,
  "HideParticles": false,
  "HidePunctuation": true,
  "MinimumTokenLength": 1,
  "CopyDictionaryForm": false,
  "OverlayPadding": 3.0,
  "Hotkey": "CTRL+SHIFT+J",
  "PrimaryMonitorOnly": true
}
```

| Setting | Description |
|---|---|
| `MinimumOcrConfidence` | Ignore OCR lines below this confidence (0–1) |
| `OcrScale` | Image upscale before OCR. 2.0 = double resolution. Reduce to 1.5 on 4K. |
| `HideParticles` | Hide Japanese particles (の, が, は, …) |
| `HidePunctuation` | Hide punctuation tokens |
| `MinimumTokenLength` | Tokens shorter than this are hidden |
| `CopyDictionaryForm` | Copy dictionary form instead of surface form (requires real tokenizer) |
| `OverlayPadding` | Extra pixels of padding around each overlay box |
| `Hotkey` | Global hotkey. Format: `CTRL+SHIFT+J` |

---

## Limitations

### Exclusive fullscreen applications

Applications that use a true **exclusive-mode Direct3D** swap chain (e.g., some
games with "Fullscreen" display mode) cannot be overlaid because Windows routes
rendering directly to the GPU without compositing. In this case:

- Use **Borderless Windowed** mode in the game's graphics settings — overlays
  work reliably in this mode.
- Alternatively, use Alt+Enter to toggle out of exclusive fullscreen before
  activating the OCR.

This is documented in `WindowsScreenCaptureService.cs` and is a platform
limitation, not a bug. The DXGI Desktop Duplication API (Phase 7) may capture
exclusive fullscreen frames but the overlay window will still not render on top.

### DPI scaling

Tested on 100%, 125%, and 150% DPI. If overlay boxes are misaligned:

1. Verify `app.manifest` is included in the build output.
2. Ensure no other DPI-awareness calls precede the manifest (`SetProcessDpiAwareness`
   called elsewhere would override the manifest).
3. File an issue with your DPI percentage and monitor configuration.

### OCR accuracy

`Windows.Media.Ocr` accuracy depends on:

- Font clarity and size (min ~12px for reliable recognition)
- Text contrast against background
- Image scale (the default `OcrScale: 2.0` helps small text)
- Motion blur (static/paused frames work best)
- Mixed-language text (performs well on pure Japanese; degrades with heavy Latin mix)

For higher accuracy, swap `WindowsOcrService` with a PaddleOCR or Tesseract
implementation via `IOcrService`.

### Tokenizer

The built-in `StubJapaneseTokenizer` segments text by Unicode script block
(groups of kanji, hiragana, katakana, etc.). It does **not** perform
morphological analysis — it cannot split compound verbs, conjugated forms, or
mixed kanji+hiragana words like 食べた (tabeta).

---

## Replacing the OCR Engine

1. Implement `IOcrService`:

```csharp
public sealed class PaddleOcrService : IOcrService
{
    public async Task<OcrResult> DetectJapaneseTextAsync(ScreenFrame frame, CancellationToken ct)
    {
        // Call PaddleOCR subprocess or ONNX runtime
        // Return OcrResult with Lines and OcrScale
    }
}
```

2. Register in `App.xaml.cs`:

```csharp
// Replace:
services.AddSingleton<IOcrService, WindowsOcrService>();
// With:
services.AddSingleton<IOcrService, PaddleOcrService>();
```

---

## Replacing the Japanese Tokenizer

### MeCab (subprocess)

```csharp
public sealed class MeCabTokenizer : IJapaneseTokenizer
{
    public List<JapaneseToken> Tokenize(string text)
    {
        // Pipe text to: mecab.exe --node-format="%m\t%f[0]\t%f[7]\n"
        // Parse TSV output: surface, partOfSpeech, dictionaryForm
        // Map character indices by walking the output
    }
}
```

Install: `winget install MeCab.MeCab`

### Sudachi (HTTP service)

```csharp
public sealed class SudachiTokenizer : IJapaneseTokenizer
{
    private readonly HttpClient _client = new();

    public List<JapaneseToken> Tokenize(string text)
    {
        // POST to http://localhost:8000/tokenize
        // Parse JSON array of morphemes
    }
}
```

---

## Logs

Log files are written to `%AppData%\JPLens\Logs\jplens-YYYYMMDD.log`.
The last 7 days of logs are retained.

---

## Architecture Diagram

```
Hotkey (Ctrl+Shift+J)
        │
        ▼
AppController.RunOcrOverlayFlowAsync()
        │
        ├── IScreenCaptureService.CapturePrimaryMonitor()
        │         └── GDI BitBlt → ScreenFrame (physical px bitmap)
        │
        ├── ImagePreprocessor.Preprocess(frame, ocrScale=2.0)
        │         └── Scale × 2 → Contrast boost → Sharpen
        │
        ├── IOcrService.DetectJapaneseTextAsync(frame)
        │         └── Windows.Media.Ocr → OcrResult (lines + char boxes)
        │
        ├── For each OcrLine:
        │     ├── IJapaneseTokenizer.Tokenize(line.Text)
        │     │         └── Script-group segmentation → [JapaneseToken]
        │     └── ITokenBoxMapper.MapTokensToBoxes(line, tokens, frame, scale)
        │               └── Token char indices → OCR char boxes → union → PixelRect
        │
        ├── OverlayPostProcessor.RemoveDuplicates()
        ├── OverlayPostProcessor.RemoveTinyBoxes()
        │
        └── IOverlayWindow.ShowOverlays([WordOverlay])
                  └── WPF transparent window over primary monitor
                            ├── OnRender: gold rounded-rect boxes
                            └── WM_NCHITTEST: click-through outside boxes

Click on box:
  IClipboardService.CopyText(word) → "Copied: ..." feedback
```

---

## Development

### Adding a new Phase 7 feature

1. **Multi-monitor**: `MonitorService.GetAllMonitors()` already returns all monitors.
   Loop over them in `AppController`, passing each `ScreenFrame` through the pipeline.
   Position `OverlayWindow` using per-monitor DPI scale.

2. **DXGI Desktop Duplication**: Implement `IScreenCaptureService` using
   `IDXGIOutputDuplication::AcquireNextFrame`. This captures exclusive-fullscreen
   frames that GDI cannot.

3. **Settings UI**: Add a WPF `Window` that binds to `AppSettings`. Inject
   `SettingsService` and call `Save()` on close.

---

## License

MIT
