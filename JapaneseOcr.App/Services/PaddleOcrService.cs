using System.IO;
using System.Text;
using JapaneseOcr.Interfaces;
using JapaneseOcr.Models;
using JapaneseOcr.Processing;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using OcvPoint  = OpenCvSharp.Point;
using OcvSize   = OpenCvSharp.Size;

namespace JapaneseOcr.Services;

/// <summary>
/// OCR implementation backed by PaddleOCR ONNX models (PP-OCRv3/v4).
///
/// Three-stage pipeline:
///   1. Detection  – DB text detector produces rotated bounding boxes
///   2. Classification (optional) – corrects 180° upside-down text
///   3. Recognition – CRNN/CTC decoding → text + confidence score
///
/// Required model files (default: {ExeDir}\models\paddle\):
///   det_model.onnx   PP-OCRv4 detection model
///   rec_model.onnx   japan_PP-OCRv3_rec (or v4) recognition model
///   japan_dict.txt   Japanese character dictionary (one entry per line)
///   cls_model.onnx   (optional) 0°/180° angle classifier
///
/// Exporting models from PaddleOCR (Python):
///   paddle2onnx --model_dir ./ch_PP-OCRv4_det_infer \
///               --model_filename inference.pdmodel \
///               --params_filename inference.pdiparams \
///               --save_file det_model.onnx
///   (repeat for the rec and cls models)
///
/// Japanese dict download:
///   https://github.com/PaddlePaddle/PaddleOCR/blob/release/2.7/ppocr/utils/dict/japan_dict.txt
/// </summary>
public sealed class PaddleOcrService : IOcrService, IDisposable
{
    // ── Detection hyper-parameters ─────────────────────────────────────────
    private const int   DetMaxSide     = 960;   // longest side cap before OCR
    private const int   DetStride      = 32;    // model stride (resize to multiple)
    private const float DetThresh      = 0.3f;  // probability map binarization
    private const float DetBoxThresh   = 0.6f;  // per-box score filter
    // 1.1 = small safety margin; larger values inflate boxes in all directions.
    // WindowsOCR gives tight boxes natively; PaddleOCR requires this to be low
    // or the top/bottom padding becomes visually wrong after AABB conversion.
    private const float DetUnclipRatio = 1.1f;
    private const int   DetMinSide     = 3;     // discard boxes smaller than this

    // ── Recognition hyper-parameters ──────────────────────────────────────
    private const int RecImageHeight = 48;
    private const int RecMaxWidth    = 320;

    // ── Classification hyper-parameters ───────────────────────────────────
    private const int   ClsImageHeight = 48;
    private const int   ClsImageWidth  = 192;
    private const float ClsThresh      = 0.9f;  // flip if P(180°) > this

    // Detection uses ImageNet normalization; recognition uses [-1, 1] normalization.
    private static readonly float[] DetMean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] DetStd  = { 0.229f, 0.224f, 0.225f };

    private readonly ILogger<PaddleOcrService> _logger;
    private readonly AppSettings               _settings;
    private readonly InferenceSession          _detSession;
    private readonly InferenceSession          _recSession;
    private readonly InferenceSession?         _clsSession;
    private readonly string[]                  _dict;

    private bool _disposed;

    public PaddleOcrService(
        ILogger<PaddleOcrService> logger,
        AppSettings               settings)
    {
        _logger   = logger;
        _settings = settings;

        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };

        _detSession = LoadSession(Resolve(settings.PaddleDetModelPath), opts, "detection");
        _recSession = LoadSession(Resolve(settings.PaddleRecModelPath), opts, "recognition");

        var clsPath = Resolve(settings.PaddleClsModelPath);
        if (File.Exists(clsPath))
            _clsSession = LoadSession(clsPath, opts, "classification");

        _dict = LoadDict(Resolve(settings.PaddleDictPath));

        _logger.LogInformation(
            "PaddleOcrService ready. dict={DictLen} chars | det={Det} | rec={Rec} | cls={Cls}",
            _dict.Length,
            settings.PaddleDetModelPath,
            settings.PaddleRecModelPath,
            _clsSession is null ? "(skipped)" : settings.PaddleClsModelPath);
    }

    /// <inheritdoc/>
    public async Task<OcrResult> DetectJapaneseTextAsync(
        ScreenFrame frame, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Run the CPU-bound pipeline on a thread-pool thread to keep the UI
        // thread responsive.
        var lines = await Task.Run(() => RunPipeline(frame.Image, ct), ct);

        _logger.LogInformation("PaddleOCR returned {Count} lines", lines.Count);
        return new OcrResult { Lines = lines, OcrScale = 1.0 };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _detSession.Dispose();
        _recSession.Dispose();
        _clsSession?.Dispose();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Main pipeline
    // ──────────────────────────────────────────────────────────────────────────

    private List<OcrLine> RunPipeline(System.Drawing.Bitmap bitmap, CancellationToken ct)
    {
        using var mat = BitmapToMat(bitmap);

        // ── Stage 1: Detection ────────────────────────────────────────────────
        var (detTensor, scaleH, scaleW, padH, padW) = PrepareDetInput(mat);

        var detInputName = _detSession.InputMetadata.Keys.First();
        var detInput = new DenseTensor<float>(detTensor, new[] { 1, 3, padH, padW });

        using var detOutputs = _detSession.Run(
            new[] { NamedOnnxValue.CreateFromTensor(detInputName, detInput) });

        var probArray = detOutputs.First().AsTensor<float>().ToArray();
        var rects     = PostProcessDet(probArray, padH, padW, scaleH, scaleW,
                                       mat.Rows, mat.Cols);

        _logger.LogDebug("Detection found {Count} text regions", rects.Count);

        // ── Stage 2+3: Classify (optional) + Recognise ───────────────────────
        var lines = new List<OcrLine>(rects.Count);

        foreach (var rect in rects)
        {
            ct.ThrowIfCancellationRequested();

            using var crop = WarpCrop(mat, rect);
            if (crop.Empty() || crop.Rows < 2 || crop.Cols < 2) continue;

            // Orientation classification — flip upside-down crops before recognition
            if (_clsSession is not null && IsUpsideDown(crop))
                Cv2.Rotate(crop, crop, RotateFlags.Rotate180);

            var (text, confidence) = RunRecognition(crop);
            if (string.IsNullOrEmpty(text)) continue;
            if (confidence < (float)_settings.MinimumOcrConfidence) continue;

            var normalized  = TextNormalizer.Normalize(text);
            if (string.IsNullOrEmpty(normalized)) continue;

            var lineBox = RotatedRectToPixelRect(rect);

            // Derive orientation from the axis-aligned bounding box dimensions.
            // rect.Size.Width/Height from MinAreaRect is unreliable — for angles
            // near -90° OpenCV returns Width as the long (vertical) axis, so a
            // simple Height>Width comparison produces wrong results for vertical
            // text columns.  The axis-aligned lineBox dimensions are unambiguous.
            var orientation = lineBox.Height > lineBox.Width * 1.5
                ? TextOrientation.Vertical
                : lineBox.Width > lineBox.Height * 1.5
                    ? TextOrientation.Horizontal
                    : TextOrientation.Unknown;

            var characters = CharacterBoxEstimator.Estimate(normalized, lineBox, orientation);

            _logger.LogDebug(
                "Recognised: '{T}' (conf={C:F2}) box={B}",
                normalized, confidence, lineBox);

            lines.Add(new OcrLine
            {
                Text        = normalized,
                BoundingBox = lineBox,
                Confidence  = confidence,
                Characters  = characters,
                Orientation = orientation,
            });
        }

        return lines;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Detection
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resizes the source image so its longest side fits within DetMaxSide, pads
    /// both dimensions to the nearest DetStride multiple, then builds a CHW
    /// float32 tensor with ImageNet normalization (BGR→RGB swap included).
    ///
    /// Returns the tensor, the H/W scale factors used (so bounding boxes can be
    /// mapped back to original-image coordinates), and the padded H/W dimensions.
    /// </summary>
    private static (float[] tensor, double scaleH, double scaleW, int padH, int padW)
        PrepareDetInput(Mat src)
    {
        int h = src.Rows, w = src.Cols;

        double ratio = Math.Min(1.0, (double)DetMaxSide / Math.Max(h, w));
        int newH = (int)Math.Round(h * ratio);
        int newW = (int)Math.Round(w * ratio);

        int padH = Math.Max(DetStride, ((newH + DetStride - 1) / DetStride) * DetStride);
        int padW = Math.Max(DetStride, ((newW + DetStride - 1) / DetStride) * DetStride);

        double scaleH = (double)padH / h;
        double scaleW = (double)padW / w;

        using var resized = new Mat();
        Cv2.Resize(src, resized, new OcvSize(padW, padH));

        int planeSize = padH * padW;
        var tensor    = new float[3 * planeSize];

        unsafe
        {
            byte* ptr = (byte*)resized.Data;
            int   step = (int)resized.Step();

            for (int y = 0; y < padH; y++)
            for (int x = 0; x < padW; x++)
            {
                byte bv = ptr[y * step + x * 3 + 0];
                byte gv = ptr[y * step + x * 3 + 1];
                byte rv = ptr[y * step + x * 3 + 2];

                int flat = y * padW + x;
                // BGR source → RGB channel order for the model
                tensor[0 * planeSize + flat] = (rv / 255f - DetMean[0]) / DetStd[0];
                tensor[1 * planeSize + flat] = (gv / 255f - DetMean[1]) / DetStd[1];
                tensor[2 * planeSize + flat] = (bv / 255f - DetMean[2]) / DetStd[2];
            }
        }

        return (tensor, scaleH, scaleW, padH, padW);
    }

    /// <summary>
    /// Thresholds the DB probability map, finds contours, filters by score,
    /// unclips (expands) each box, then scales back to original-image pixel space.
    /// </summary>
    private static List<RotatedRect> PostProcessDet(
        float[] probFlat,
        int mapH, int mapW,
        double scaleH, double scaleW,
        int origH, int origW)
    {
        using var probMat = new Mat(mapH, mapW, MatType.CV_32FC1);
        unsafe
        {
            float* dst = (float*)probMat.Data;
            new ReadOnlySpan<float>(probFlat, 0, mapH * mapW)
                .CopyTo(new Span<float>(dst, mapH * mapW));
        }

        // Binarize the probability map
        using var binFloat = new Mat();
        Cv2.Threshold(probMat, binFloat, DetThresh, 1.0, ThresholdTypes.Binary);
        using var binByte = new Mat();
        binFloat.ConvertTo(binByte, MatType.CV_8UC1, 255.0);

        // Small dilation merges adjacent character blobs into word-level regions
        using var kernel  = Cv2.GetStructuringElement(MorphShapes.Rect, new OcvSize(3, 3));
        using var dilated = new Mat();
        Cv2.Dilate(binByte, dilated, kernel);

        Cv2.FindContours(dilated, out OcvPoint[][] contours, out _,
            RetrievalModes.List, ContourApproximationModes.ApproxSimple);

        var results = new List<RotatedRect>(contours.Length);

        foreach (var contour in contours)
        {
            if (contour.Length < 4) continue;

            var rotRect = Cv2.MinAreaRect(contour);
            if (Math.Min(rotRect.Size.Width, rotRect.Size.Height) < DetMinSide) continue;

            float score = ComputeContourScore(probMat, contour);
            if (score < DetBoxThresh) continue;

            var expanded = UnclipRect(rotRect, DetUnclipRatio);

            // Map from detection-image space back to original-image space
            var center = new Point2f(
                Math.Clamp((float)(expanded.Center.X / scaleW), 0, origW - 1),
                Math.Clamp((float)(expanded.Center.Y / scaleH), 0, origH - 1));
            var size = new Size2f(
                (float)(expanded.Size.Width  / scaleW),
                (float)(expanded.Size.Height / scaleH));

            results.Add(new RotatedRect(center, size, expanded.Angle));
        }

        return results;
    }

    /// <summary>
    /// Average probability value of the pixels inside <paramref name="contour"/>'s
    /// bounding box, masked by the filled contour polygon.
    /// </summary>
    private static float ComputeContourScore(Mat probMap, OcvPoint[] contour)
    {
        var bbox = Cv2.BoundingRect(contour);
        int x1 = Math.Max(0, bbox.X);
        int y1 = Math.Max(0, bbox.Y);
        int x2 = Math.Min(probMap.Cols, bbox.X + bbox.Width);
        int y2 = Math.Min(probMap.Rows, bbox.Y + bbox.Height);

        if (x2 <= x1 || y2 <= y1) return 0f;

        var clipped = new Rect(x1, y1, x2 - x1, y2 - y1);

        using var mask = new Mat(clipped.Height, clipped.Width, MatType.CV_8UC1, Scalar.Black);
        var shifted = contour
            .Select(p => new OcvPoint(p.X - clipped.X, p.Y - clipped.Y))
            .ToArray();
        Cv2.FillPoly(mask, new[] { shifted }, Scalar.White);

        using var roi  = new Mat(probMap, clipped);
        var mean = Cv2.Mean(roi, mask);
        return (float)mean.Val0;
    }

    /// <summary>
    /// Expands a <see cref="RotatedRect"/> uniformly using the DB unclip formula:
    ///   offset = (Width × Height × ratio) / (2 × (Width + Height))
    /// </summary>
    private static RotatedRect UnclipRect(RotatedRect rect, float ratio)
    {
        float perimeter = 2f * (rect.Size.Width + rect.Size.Height);
        if (perimeter < float.Epsilon) return rect;

        float offset = rect.Size.Width * rect.Size.Height * ratio / perimeter;
        return new RotatedRect(
            rect.Center,
            new Size2f(rect.Size.Width + 2 * offset, rect.Size.Height + 2 * offset),
            rect.Angle);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Crop extraction (perspective warp)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Perspective-warps the text region into a flat, horizontally-oriented Mat.
    /// If the resulting crop is taller than 1.5× its width (vertical text block),
    /// it is transposed and flipped to horizontal orientation before returning.
    /// </summary>
    private static Mat WarpCrop(Mat src, RotatedRect rect)
    {
        var pts = rect.Points(); // four corners, order may vary

        // Sort corners into: top-left, top-right, bottom-right, bottom-left
        var byY = pts.OrderBy(p => p.Y).ToArray();
        Point2f tl, tr, bl, br;
        if (byY[0].X <= byY[1].X) { tl = byY[0]; tr = byY[1]; }
        else                       { tl = byY[1]; tr = byY[0]; }
        if (byY[2].X <= byY[3].X) { bl = byY[2]; br = byY[3]; }
        else                       { bl = byY[3]; br = byY[2]; }

        static double Dist(Point2f a, Point2f b) =>
            Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        int cropW = Math.Max(1, (int)Math.Round(Math.Max(Dist(tl, tr), Dist(bl, br))));
        int cropH = Math.Max(1, (int)Math.Round(Math.Max(Dist(tl, bl), Dist(tr, br))));

        static Point2f Clamp(Point2f p, int maxX, int maxY) =>
            new(Math.Clamp(p.X, 0f, maxX - 1f), Math.Clamp(p.Y, 0f, maxY - 1f));

        var srcPts = new[]
        {
            Clamp(tl, src.Cols, src.Rows),
            Clamp(tr, src.Cols, src.Rows),
            Clamp(br, src.Cols, src.Rows),
            Clamp(bl, src.Cols, src.Rows),
        };

        var dstPts = new Point2f[]
        {
            new(0,          0),
            new(cropW - 1,  0),
            new(cropW - 1,  cropH - 1),
            new(0,          cropH - 1),
        };

        using var M = Cv2.GetPerspectiveTransform(srcPts, dstPts);
        var crop = new Mat();
        Cv2.WarpPerspective(src, crop, M, new OcvSize(cropW, cropH),
            InterpolationFlags.Linear, BorderTypes.Replicate);

        // Vertical text block → rotate to horizontal so the rec model works correctly
        if (cropH > cropW * 1.5)
        {
            Cv2.Transpose(crop, crop);
            Cv2.Flip(crop, crop, FlipMode.X);
        }

        return crop;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Angle classification (optional, skip when _clsSession is null)
    // ──────────────────────────────────────────────────────────────────────────

    private bool IsUpsideDown(Mat crop)
    {
        using var resized = new Mat();
        Cv2.Resize(crop, resized, new OcvSize(ClsImageWidth, ClsImageHeight));

        var tensor    = NormalizeRec(resized, ClsImageHeight, ClsImageWidth);
        var inputName = _clsSession!.InputMetadata.Keys.First();
        var t         = new DenseTensor<float>(tensor, new[] { 1, 3, ClsImageHeight, ClsImageWidth });

        using var outputs = _clsSession.Run(
            new[] { NamedOnnxValue.CreateFromTensor(inputName, t) });

        // Output shape [1, 2]: index 0 = P(0°), index 1 = P(180°)
        var scores = outputs.First().AsTensor<float>().ToArray();
        return scores.Length >= 2 && scores[1] > ClsThresh;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Recognition
    // ──────────────────────────────────────────────────────────────────────────

    private (string text, float confidence) RunRecognition(Mat crop)
    {
        int h = crop.Rows, w = crop.Cols;
        int recW = Math.Clamp(
            (int)Math.Ceiling((double)w / h * RecImageHeight),
            1, RecMaxWidth);

        using var resized = new Mat();
        Cv2.Resize(crop, resized, new OcvSize(recW, RecImageHeight));

        var tensor    = NormalizeRec(resized, RecImageHeight, recW);
        var inputName = _recSession.InputMetadata.Keys.First();
        var t         = new DenseTensor<float>(tensor, new[] { 1, 3, RecImageHeight, recW });

        using var outputs = _recSession.Run(
            new[] { NamedOnnxValue.CreateFromTensor(inputName, t) });

        var logitsTensor = outputs.First().AsTensor<float>();

        // Output shape: [1, T, C]  T = time steps, C = num_classes (dict size + 1 blank)
        if (logitsTensor.Dimensions.Length < 3) return (string.Empty, 0f);

        int timeSteps  = logitsTensor.Dimensions[1];
        int numClasses = logitsTensor.Dimensions[2];
        var logits     = logitsTensor.ToArray();

        return CtcDecode(logits, timeSteps, numClasses);
    }

    /// <summary>
    /// PP-OCR recognition normalization: (pixel / 255 − 0.5) / 0.5 per channel.
    /// Input Mat is BGR; output tensor uses RGB channel ordering (CHW layout).
    /// </summary>
    private static float[] NormalizeRec(Mat src, int h, int w)
    {
        int planeSize = h * w;
        var tensor    = new float[3 * planeSize];

        unsafe
        {
            byte* ptr  = (byte*)src.Data;
            int   step = (int)src.Step();

            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte bv = ptr[y * step + x * 3 + 0];
                byte gv = ptr[y * step + x * 3 + 1];
                byte rv = ptr[y * step + x * 3 + 2];

                int flat = y * w + x;
                // BGR source → RGB channel order
                tensor[0 * planeSize + flat] = (rv / 255f - 0.5f) / 0.5f;
                tensor[1 * planeSize + flat] = (gv / 255f - 0.5f) / 0.5f;
                tensor[2 * planeSize + flat] = (bv / 255f - 0.5f) / 0.5f;
            }
        }

        return tensor;
    }

    /// <summary>
    /// Greedy CTC decoder.
    /// Blank token = index 0; <c>_dict[i]</c> maps to class index <c>i + 1</c>.
    /// The model's final layer is a softmax, so each time-step's max value is
    /// already a probability in [0, 1] — used directly as per-character confidence.
    /// </summary>
    private (string text, float confidence) CtcDecode(
        float[] probs, int timeSteps, int numClasses)
    {
        var sb        = new StringBuilder();
        float confSum = 0f;
        int   valid   = 0;
        int   prev    = -1;

        for (int t = 0; t < timeSteps; t++)
        {
            int   argmax = 0;
            float maxVal = float.NegativeInfinity;

            for (int c = 0; c < numClasses; c++)
            {
                float v = probs[t * numClasses + c];
                if (v > maxVal) { maxVal = v; argmax = c; }
            }

            if (argmax != prev && argmax != 0) // 0 = blank token
            {
                int dictIdx = argmax - 1;
                if (dictIdx < _dict.Length)
                    sb.Append(_dict[dictIdx]);
                confSum += maxVal;
                valid++;
            }

            prev = argmax;
        }

        float confidence = valid > 0 ? confSum / valid : 0f;
        return (sb.ToString(), confidence);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a GDI+ Bitmap (Format32bppArgb screen capture) to an OpenCV Mat
    /// in BGR 24-bit format. The alpha channel is discarded (GDI BitBlt sets it
    /// to 0 for screen captures, so it carries no information).
    /// </summary>
    private static unsafe Mat BitmapToMat(System.Drawing.Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var mat = new Mat(h, w, MatType.CV_8UC3);

        var bmpData = bmp.LockBits(
            new System.Drawing.Rectangle(0, 0, w, h),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            byte* src  = (byte*)bmpData.Scan0.ToPointer();
            byte* dst  = (byte*)mat.Data;
            int srcStep = bmpData.Stride;
            int dstStep = (int)mat.Step();

            for (int y = 0; y < h; y++)
            {
                byte* srcRow = src + y * srcStep;
                byte* dstRow = dst + y * dstStep;
                for (int x = 0; x < w; x++)
                {
                    // BGRA → BGR (alpha dropped)
                    dstRow[x * 3 + 0] = srcRow[x * 4 + 0]; // B
                    dstRow[x * 3 + 1] = srcRow[x * 4 + 1]; // G
                    dstRow[x * 3 + 2] = srcRow[x * 4 + 2]; // R
                }
            }
        }
        finally
        {
            bmp.UnlockBits(bmpData);
        }

        return mat;
    }

    /// <summary>
    /// Converts a <see cref="RotatedRect"/> to a <see cref="PixelRect"/>.
    ///
    /// The naive axis-aligned bounding box (AABB) inflates the minor dimension
    /// (text height for horizontal, text width for vertical) whenever the text
    /// is even slightly tilted.  For example a 200×20px box at 5° produces an
    /// AABB that is ~37px tall — nearly double the actual glyph height.
    ///
    /// Fix: keep the AABB extent on the major axis (full line length) but
    /// replace the minor axis extent with <c>min(r.Size.Width, r.Size.Height)</c>,
    /// which is always the actual text thickness regardless of which side
    /// OpenCV happened to label Width vs Height for this angle.
    /// </summary>
    private static PixelRect RotatedRectToPixelRect(RotatedRect r)
    {
        var pts  = r.Points();
        float x1 = pts.Min(p => p.X);
        float y1 = pts.Min(p => p.Y);
        float x2 = pts.Max(p => p.X);
        float y2 = pts.Max(p => p.Y);

        float aabbW = x2 - x1;
        float aabbH = y2 - y1;

        // Minor dimension = actual text thickness (independent of angle convention)
        float minor = Math.Min(r.Size.Width, r.Size.Height);

        if (aabbW >= aabbH) // horizontal dominant → constrain height
        {
            float cy = r.Center.Y;
            return new PixelRect(x1, cy - minor / 2.0, aabbW, minor);
        }
        else               // vertical dominant → constrain width
        {
            float cx = r.Center.X;
            return new PixelRect(cx - minor / 2.0, y1, minor, aabbH);
        }
    }

    /// <summary>
    /// Resolves a potentially-relative model path against the executable directory.
    /// </summary>
    private static string Resolve(string path) =>
        Path.IsPathFullyQualified(path)
            ? path
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));

    private InferenceSession LoadSession(string path, SessionOptions opts, string label)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"PaddleOCR {label} model not found: {path}\n" +
                "Export the model with paddle2onnx and place it in models\\paddle\\.",
                path);

        _logger.LogDebug("Loading PaddleOCR {Label} model from {Path}", label, path);
        return new InferenceSession(path, opts);
    }

    private static string[] LoadDict(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"PaddleOCR dictionary not found: {path}\n" +
                "Download japan_dict.txt from the PaddleOCR repository and place it in models\\paddle\\.",
                path);

        return File.ReadAllLines(path, Encoding.UTF8)
            .Where(l => l.Length > 0)
            .ToArray();
    }
}
