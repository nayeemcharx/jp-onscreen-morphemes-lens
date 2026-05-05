using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace JapaneseOcr.Processing;

/// <summary>
/// Preprocesses a captured frame before it is passed to the OCR engine.
///
/// IMPORTANT — less is more for screen text:
///   Windows.Media.Ocr is already tuned for native-resolution screen captures
///   (it is the same engine behind Win+Shift+T). Aggressive contrast or
///   sharpening actively hurts recognition quality because:
///     • ClearType sub-pixel hints are clipped by contrast boosting.
///     • The Laplacian sharpen kernel creates ringing around thin kanji strokes.
///
///   The recommended pipeline for screen OCR is: upscale only (scale ≤ 2.0,
///   clamped so neither dimension exceeds MaxOcrDimension).  Contrast and
///   sharpening are opt-in via AppSettings and default to disabled.
/// </summary>
public static class ImagePreprocessor
{
    /// <summary>
    /// Hard pixel-dimension ceiling imposed by Windows.Media.Ocr.
    /// Exceeding this causes silent quality degradation or partial recognition.
    /// (Documented limit: 4096×4096.)
    /// </summary>
    public const int MaxOcrDimension = 4096;

    /// <summary>
    /// Computes the largest scale factor that keeps both image dimensions
    /// within <see cref="MaxOcrDimension"/>.
    /// </summary>
    public static double ClampScale(int sourceWidth, int sourceHeight, double requestedScale)
    {
        double maxSafe = (double)MaxOcrDimension / Math.Max(sourceWidth, sourceHeight);
        return Math.Min(requestedScale, maxSafe);
    }

    /// <summary>
    /// Runs the preprocessing pipeline.
    /// </summary>
    /// <param name="source">Source bitmap (any pixel format).</param>
    /// <param name="scale">Upscale factor. 1.0 = no change; 2.0 = double resolution.</param>
    /// <param name="contrastFactor">
    /// Contrast multiplier. 1.0 = identity (recommended for screen text).
    /// Values above ~1.1 degrade ClearType-rendered text.
    /// </param>
    /// <param name="sharpen">
    /// Apply a mild 3×3 unsharp-mask after scaling.
    /// Disabled by default — creates noise around fine Japanese strokes.
    /// </param>
    /// <returns>A preprocessed bitmap ready for OCR.</returns>
    public static Bitmap Preprocess(
        Bitmap source,
        double scale,
        float  contrastFactor = 1.0f,
        bool   sharpen        = false)
    {
        // Step 1: Ensure 24-bit RGB (OCR engines prefer simple RGB)
        var rgb = ConvertToRgb(source);

        // Step 2: Upscale (if requested) using high-quality bicubic interpolation
        Bitmap scaled = scale != 1.0 ? Resize(rgb, scale) : rgb;
        if (!ReferenceEquals(rgb, source))
            rgb.Dispose();

        // Step 3 (opt-in): Boost contrast — disabled by default for screen text
        if (contrastFactor != 1.0f)
        {
            var contrasted = ApplyContrast(scaled, contrastFactor);
            if (!ReferenceEquals(scaled, source))
                scaled.Dispose();
            scaled = contrasted;
        }

        // Step 4 (opt-in): Sharpen — disabled by default for screen text
        if (sharpen)
        {
            var sharpened = Sharpen(scaled);
            scaled.Dispose();
            return sharpened;
        }

        return scaled;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Pipeline steps (public so they can be tested or reused independently)
    // ──────────────────────────────────────────────────────────────────────────

    public static Bitmap ConvertToRgb(Bitmap source)
    {
        if (source.PixelFormat == PixelFormat.Format24bppRgb)
            return (Bitmap)source.Clone();

        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(result);
        g.DrawImage(source, 0, 0, source.Width, source.Height);
        return result;
    }

    public static Bitmap Resize(Bitmap source, double scale)
    {
        int newW = (int)Math.Round(source.Width  * scale);
        int newH = (int)Math.Round(source.Height * scale);

        var result = new Bitmap(newW, newH, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(result);
        g.InterpolationMode  = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode      = SmoothingMode.HighQuality;
        g.PixelOffsetMode    = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.DrawImage(source, 0, 0, newW, newH);
        return result;
    }

    /// <summary>
    /// Applies a contrast adjustment via a ColorMatrix transform.
    /// <paramref name="contrastFactor"/> &gt; 1 increases contrast; &lt; 1 decreases it.
    /// </summary>
    public static Bitmap ApplyContrast(Bitmap source, float contrastFactor)
    {
        // t = translation offset that keeps mid-grey at 0.5 after scaling
        float t = (1.0f - contrastFactor) / 2.0f;

        var colorMatrix = new ColorMatrix(
        [
            [contrastFactor, 0, 0, 0, 0],
            [0, contrastFactor, 0, 0, 0],
            [0, 0, contrastFactor, 0, 0],
            [0, 0, 0,              1, 0],
            [t, t, t,              0, 1],
        ]);

        var attributes = new ImageAttributes();
        attributes.SetColorMatrix(colorMatrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);

        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(result);
        var destRect = new Rectangle(0, 0, source.Width, source.Height);
        g.DrawImage(source, destRect, 0, 0, source.Width, source.Height,
                    GraphicsUnit.Pixel, attributes);
        return result;
    }

    /// <summary>
    /// Applies a mild 3×3 unsharp-mask sharpen kernel.
    /// Uses unsafe pointer arithmetic for performance.
    /// </summary>
    public static unsafe Bitmap Sharpen(Bitmap source)
    {
        int w = source.Width;
        int h = source.Height;

        var result  = new Bitmap(w, h, PixelFormat.Format24bppRgb);

        var srcData = source.LockBits(
            new Rectangle(0, 0, w, h),
            ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);

        var dstData = result.LockBits(
            new Rectangle(0, 0, w, h),
            ImageLockMode.WriteOnly,
            PixelFormat.Format24bppRgb);

        try
        {
            // Sharpen kernel:  0 -1  0 / -1  5 -1 / 0 -1  0
            int stride = srcData.Stride;
            byte* src  = (byte*)srcData.Scan0;
            byte* dst  = (byte*)dstData.Scan0;

            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    int i = y * stride + x * 3;

                    for (int c = 0; c < 3; c++)
                    {
                        int val = 5  * src[i + c]
                                - src[(y - 1) * stride + x       * 3 + c]
                                - src[(y + 1) * stride + x       * 3 + c]
                                - src[y       * stride + (x - 1) * 3 + c]
                                - src[y       * stride + (x + 1) * 3 + c];

                        dst[i + c] = (byte)Math.Clamp(val, 0, 255);
                    }
                }
            }

            // Copy border pixels unchanged
            CopyBorder(src, dst, w, h, stride);
        }
        finally
        {
            source.UnlockBits(srcData);
            result.UnlockBits(dstData);
        }

        return result;
    }

    private static unsafe void CopyBorder(byte* src, byte* dst, int w, int h, int stride)
    {
        // Top and bottom rows
        for (int x = 0; x < w; x++)
        {
            int top = x * 3;
            int bot = (h - 1) * stride + x * 3;
            for (int c = 0; c < 3; c++)
            {
                dst[top + c] = src[top + c];
                dst[bot + c] = src[bot + c];
            }
        }
        // Left and right columns
        for (int y = 0; y < h; y++)
        {
            int left  = y * stride;
            int right = y * stride + (w - 1) * 3;
            for (int c = 0; c < 3; c++)
            {
                dst[left  + c] = src[left  + c];
                dst[right + c] = src[right + c];
            }
        }
    }
}
