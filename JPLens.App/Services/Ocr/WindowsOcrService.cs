using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using JPLens.Interfaces;
using JPLens.Models;
using JPLens.Processing;
using Microsoft.Extensions.Logging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using WinOcr      = Windows.Media.Ocr;
using OcrEngine   = Windows.Media.Ocr.OcrEngine;
using OcrResult_Win = Windows.Media.Ocr.OcrResult;
using WinOcrLine  = Windows.Media.Ocr.OcrLine;
using WinOcrWord  = Windows.Media.Ocr.OcrWord;

namespace JPLens.Services;

/// <summary>
/// OCR implementation using the built-in Windows.Media.Ocr engine.
///
/// Requirements:
///   - Windows 10 1803+ (SDK 17134+)
///   - Japanese language pack installed:
///       Settings → Time &amp; language → Language → Add Japanese
///       (or via: dism /Online /Add-Capability /CapabilityName:Language.OCR~~~ja-JP~0.0.1.0)
///
/// The engine accepts a SoftwareBitmap and returns word-level bounding boxes.
/// For Japanese, each "word" is typically one or a few characters, which we
/// treat as OcrCharacter entries for fine-grained token mapping.
///
/// Image size limit: Windows.Media.Ocr performs best with images ≤ 5000×5000 px.
/// With OcrScale=2 on a 4K monitor the upscaled image is ~7680×4320. The engine
/// should still handle this but may be slower; reduce OcrScale to 1.5 if needed.
/// </summary>
public sealed class WindowsOcrService : IOcrService
{
    private readonly ILogger<WindowsOcrService> _logger;
    private readonly AppSettings                _settings;

    // Lazy-initialized so that the engine is only created when first needed,
    // and so that startup failures are reported at call time, not DI time.
    private OcrEngine? _engine;

    public WindowsOcrService(ILogger<WindowsOcrService> logger, AppSettings settings)
    {
        _logger   = logger;
        _settings = settings;
    }

    /// <inheritdoc/>
    public async Task<OcrResult> DetectJapaneseTextAsync(
        ScreenFrame     frame,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Lazy-initialize the OCR engine
        _engine ??= CreateEngine();

        // Determine whether any preprocessing is actually needed.
        // If scale=1.0, contrast=1.0, and sharpening is off we send the raw
        // captured bitmap straight to the OCR engine — identical to Win+Shift+T.
        double ocrScale = ImagePreprocessor.ClampScale(
            frame.Image.Width, frame.Image.Height, _settings.OcrScale);

        bool needsPreprocess = ocrScale != 1.0
            || _settings.OcrContrast  != 1.0f
            || _settings.OcrSharpening
            || _settings.OcrGrayscale;

        if (needsPreprocess)
            _logger.LogInformation(
                "OcrScale clamped {R:F2}→{E:F2} to stay within {M}px limit " +
                "(source {W}×{H})",
                _settings.OcrScale, ocrScale, ImagePreprocessor.MaxOcrDimension,
                frame.Image.Width, frame.Image.Height);

        // Split the captured frame into 6 overlapping slices and run OCR on each.
        // Using sub-images gives the OCR engine more local context and a larger
        // relative text size, improving recognition for small or edge text.
        // All slices use the same ocrScale (computed from the full frame) so that
        // every bounding box lives in the same coordinate space after the offset
        // correction below.  Duplicates produced by the overlap are eliminated by
        // OverlayPostProcessor.RemoveDuplicates after tokenisation.
        var slices   = GetSlices(frame.Image.Width, frame.Image.Height, _settings.OcrMultipleCrops, _settings.OcrVertical);
        _logger.LogDebug("Screen capture split into {N} slices (multipleCrops={M}, vertical={V})",
            slices.Count, _settings.OcrMultipleCrops, _settings.OcrVertical);
        _logger.LogInformation("Running OCR on {N} slices (preprocessed={P})",
            slices.Count, needsPreprocess);
        var allLines = new List<OcrLine>();

        for (int si = 0; si < slices.Count; si++)
        {
            ct.ThrowIfCancellationRequested();

            var (sliceX, sliceY, sliceW, sliceH) = slices[si];

            using var subBitmap = CropBitmap(frame.Image, sliceX, sliceY, sliceW, sliceH);

            System.Drawing.Bitmap? preprocessed = needsPreprocess
                ? ImagePreprocessor.Preprocess(
                    subBitmap,
                    ocrScale,
                    contrastFactor: _settings.OcrContrast,
                    sharpen:        _settings.OcrSharpening,
                    grayscale:      _settings.OcrGrayscale)
                : null;

            var bitmapForOcr = preprocessed ?? subBitmap;

            _logger.LogDebug(
                "Slice {I}/{N} ({X},{Y}) {SW}×{SH} → OCR image {OW}×{OH}",
                si + 1, slices.Count, sliceX, sliceY, sliceW, sliceH,
                bitmapForOcr.Width, bitmapForOcr.Height);
            // SaveDebugImage(bitmapForOcr);
            using var softwareBitmap = await ConvertToSoftwareBitmapAsync(bitmapForOcr);
            preprocessed?.Dispose();

            ct.ThrowIfCancellationRequested();

            var rawResult = await _engine.RecognizeAsync(softwareBitmap);
            var lines     = BuildLines(rawResult, ocrScale);

            // Shift bounding boxes from sub-image OCR space → full-image OCR space.
            // The OCR engine returns coordinates in the preprocessed image space
            // (original × ocrScale). Adding (sliceX × ocrScale, sliceY × ocrScale)
            // maps them back to the equivalent position in the full-frame OCR image.
            if (sliceX != 0 || sliceY != 0)
                lines = ApplyOffset(lines, sliceX * ocrScale, sliceY * ocrScale);

            allLines.AddRange(lines);
        }

        _logger.LogInformation(
            "OCR returned {Count} lines total across {S} slices",
            allLines.Count, slices.Count);

        return new OcrResult
        {
            Lines    = allLines,
            OcrScale = ocrScale,
        };
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the 6 slice rectangles used for multi-pass OCR.
    /// When <paramref name="vertical"/> is false: 7 horizontal bands (3 thirds + 4 quarters).
    /// When <paramref name="vertical"/> is true:  7 vertical bands (3 thirds + 4 quarters).
    /// </summary>
    private static List<(int X, int Y, int W, int H)> GetSlices(int width, int height, bool ocrMultipleCrops, bool vertical)
    {
        
        if(!ocrMultipleCrops)
        {
            return
            [
                (0, 0, width, height)
            ];
        }
       
        if (vertical)
        {
            int w3 = width / 3;
            int w4 = width / 4;

            return
            [
                // Vertical thirds (1/3 width, full height each)
                (0,       0,  w3,             height),
                (w3,      0,  w3,             height),
                (w3 * 2,  0,  width - w3 * 2, height),

                // Vertical quarters (1/4 width, full height each)
                (0,       0,  w4,             height),
                (w4,      0,  w4,             height),
                (w4 * 2,  0,  w4,             height),
                (w4 * 3,  0,  width - w4 * 3, height),
            ];
        }
        else
        {
            int h3 = height / 3;
            int h4 = height / 4;

            return
            [
                // Horizontal thirds (full width, 1/3 height each)
                (0,  0,       width,  h3            ),
                (0,  h3,      width,  h3            ),
                (0,  h3 * 2,  width,  height - h3*2 ),

                // Horizontal quarters (full width, 1/4 height each)
                (0,  0,       width,  h4            ),
                (0,  h4,      width,  h4            ),
                (0,  h4 * 2,  width,  h4            ),
                (0,  h4 * 3,  width,  height - h4*3 ),
                
            ];
        }
    }

    /// <summary>Creates a cropped copy of <paramref name="source"/>.</summary>
    private static System.Drawing.Bitmap CropBitmap(
        System.Drawing.Bitmap source, int x, int y, int w, int h)
        => source.Clone(
            new System.Drawing.Rectangle(x, y, w, h),
            source.PixelFormat);

    /// <summary>
    /// Returns a new list of lines with every bounding box shifted by
    /// (<paramref name="dx"/>, <paramref name="dy"/>) in OCR-image pixel space.
    /// </summary>
    private static List<OcrLine> ApplyOffset(List<OcrLine> lines, double dx, double dy)
    {
        var result = new List<OcrLine>(lines.Count);
        foreach (var line in lines)
        {
            result.Add(new OcrLine
            {
                Text        = line.Text,
                BoundingBox = OffsetRect(line.BoundingBox, dx, dy),
                Confidence  = line.Confidence,
                Orientation = line.Orientation,
                Characters  = line.Characters
                    .Select(c => new OcrCharacter
                    {
                        Text        = c.Text,
                        BoundingBox = OffsetRect(c.BoundingBox, dx, dy),
                        Confidence  = c.Confidence,
                    })
                    .ToList(),
            });
        }
        return result;
    }

    private static PixelRect OffsetRect(PixelRect r, double dx, double dy)
        => new(r.X + dx, r.Y + dy, r.Width, r.Height);

    private void SaveDebugImage(System.Drawing.Bitmap bitmap)
    {
        try
        {
            var dir = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "images"));
            Directory.CreateDirectory(dir);

            var fileName = $"ocr_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
            var filePath = Path.Combine(dir, fileName);
            bitmap.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
            _logger.LogDebug("Debug image saved → {Path}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save debug image");
        }
    }

    private OcrEngine CreateEngine()
    {
        var language = new Language("ja");
        var engine   = OcrEngine.TryCreateFromLanguage(language);

        if (engine is null)
            throw new InvalidOperationException(
                "Japanese OCR language pack is not installed. " +
                "Go to Settings → Time & language → Language and add Japanese, " +
                "or run: dism /Online /Add-Capability " +
                "/CapabilityName:Language.OCR~~~ja-JP~0.0.1.0");

        _logger.LogInformation("Windows.Media.Ocr engine initialised for 'ja'");
        return engine;
    }

    private List<OcrLine> BuildLines(OcrResult_Win rawResult, double ocrScale)
    {
        // NOTE: 'OcrResult_Win' is the Windows.Media.Ocr.OcrResult — aliased
        //       below to avoid naming conflict with our own OcrResult model.
        var lines = new List<OcrLine>(rawResult.Lines.Count);

        foreach (var rawLine in rawResult.Lines)
        {
            // Windows OCR does not expose per-line confidence; default to 1.0
            const double lineConfidence = 1.0;

            // Build character list from word-level boxes.
            // In Japanese mode each "word" is typically 1–3 characters.
            var characters = new List<OcrCharacter>(rawLine.Words.Count);
            foreach (var word in rawLine.Words)
            {
                characters.Add(new OcrCharacter
                {
                    Text        = word.Text,
                    BoundingBox = ToPixelRect(word.BoundingRect),
                    Confidence  = 1.0,
                });
                // _logger.LogDebug("  Word: '{T}' → Box: {B}", word.Text, word.BoundingRect);
            }

            // Derive line bounding box as the union of all word boxes
            var lineBox = characters.Count > 0
                ? GeometryHelper.Union(characters.Select(c => c.BoundingBox).ToList())
                : ToPixelRect(rawLine.Words.Count > 0
                    ? rawLine.Words[0].BoundingRect
                    : new Windows.Foundation.Rect());
            
            var normalized = TextNormalizer.Normalize(rawLine.Text);

            // Windows.Media.Ocr inserts ASCII spaces between its internal "word"
            // clusters when reporting Japanese text.  Those spaces are OCR
            // artefacts — Japanese prose does not use word-separating spaces —
            // and they cause the character-index map in TokenBoxMapper to go out
            // of sync with the Characters list (which contains no space entries).
            // Stripping them here makes line.Text and the Characters list
            // perfectly aligned: sum(ch.Text.Length for ch in Characters) == line.Text.Length.
            var lineText = normalized.Replace(" ", "");
            _logger.LogDebug("Raw line text: '{T}' → Normalized line text: '{N}' and bounding box: {B}", rawLine.Text, lineText, lineBox);
            // Estimate character boxes when Windows OCR gives only word-level
            // (this is the fallback if 'characters' is empty)
            if (characters.Count == 0)
            {
                var orientation = EstimateOrientation(rawLine);
                characters = CharacterBoxEstimator.Estimate(normalized, lineBox, orientation);
            }

            lines.Add(new OcrLine
            {
                Text        = lineText,
                BoundingBox = lineBox,
                Confidence  = lineConfidence,
                Characters  = characters,
                Orientation = EstimateOrientation(rawLine),
            });
        }

        return lines;
    }

    /// <summary>
    /// Infers reading direction from the geometric arrangement of words in a line.
    /// </summary>
    private static TextOrientation EstimateOrientation(WinOcrLine line)
    {
        if (line.Words.Count < 2)
            return TextOrientation.Horizontal; // default

        var first = line.Words[0].BoundingRect;
        var last  = line.Words[^1].BoundingRect;

        double deltaX = Math.Abs(last.X - first.X);
        double deltaY = Math.Abs(last.Y - first.Y);

        if (deltaY > deltaX * 1.5) return TextOrientation.Vertical;
        if (deltaX > deltaY * 1.5) return TextOrientation.Horizontal;
        return TextOrientation.Unknown;
    }

    private static PixelRect ToPixelRect(Windows.Foundation.Rect r)
        => new((double)r.X, (double)r.Y, (double)r.Width, (double)r.Height);

    // ──────────────────────────────────────────────────────────────────────────
    // GDI Bitmap → SoftwareBitmap
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a GDI+ Bitmap to a WinRT SoftwareBitmap.
    ///
    /// Key detail — BitmapAlphaMode.Ignore:
    ///   GDI <c>CopyFromScreen</c> (BitBlt) does NOT write the alpha channel of
    ///   a Format32bppArgb destination — every pixel has alpha = 0.
    ///   Using <c>Premultiplied</c> would multiply every RGB channel by 0,
    ///   producing a fully-black image.  <c>Ignore</c> tells the decoder to
    ///   treat all pixels as fully opaque regardless of the stored alpha byte,
    ///   so the RGB values captured from the screen are passed to the OCR engine
    ///   unchanged — identical in effect to Win+Shift+T, which gets alpha=255
    ///   from the GPU compositor and therefore is unaffected by this issue.
    /// </summary>
    private static async Task<SoftwareBitmap> ConvertToSoftwareBitmapAsync(
        System.Drawing.Bitmap bitmap)
    {
        using var memStream = new MemoryStream();
        bitmap.Save(memStream, System.Drawing.Imaging.ImageFormat.Bmp);
        memStream.Position = 0;

        using var ras = memStream.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(ras);

        // Ignore: alpha is treated as 255 for every pixel.
        // This neutralises the BitBlt alpha=0 issue without any pixel manipulation.
        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore);
    }
}
