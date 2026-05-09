using JPLens.Models;

namespace JPLens.Processing;

/// <summary>
/// Post-processes the list of <see cref="WordOverlay"/> items produced by the
/// token mapping pipeline to remove near-duplicate and unusably small boxes.
/// </summary>
public static class OverlayPostProcessor
{
    // Minimum box dimension (physical pixels) to be considered displayable
    private const double MinDimension = 4.0;

    // IoU threshold above which two boxes with the same text are considered
    // duplicates (the smaller-area box is discarded)
    private const double DuplicateIoUThreshold = 0.0001;

    // MinContainment threshold: if the smaller of two same-text boxes has more
    // than this fraction of its area covered by the other box, treat it as a
    // duplicate.  Catches cases where a small box sits mostly inside a large
    // box — IoU would be diluted by the large union area and miss these.
    private const double DuplicateContainmentThreshold = 0.1;

    /// <summary>
    /// Removes overlays whose surface text and screen bounding box are nearly
    /// identical to an already-accepted overlay.
    /// O(n²) — acceptable for typical word counts (&lt;500 per frame).
    /// </summary>
    public static List<WordOverlay> RemoveDuplicates(IReadOnlyList<WordOverlay> overlays)
    {
        var result = new List<WordOverlay>(overlays.Count);

        foreach (var candidate in overlays)
        {
            bool isDuplicate = false;

            foreach (var accepted in result)
            {
                
                double containment = GeometryHelper.MinContainment(
                        candidate.ScreenBoundingBox,
                        accepted.ScreenBoundingBox);

                if(containment > DuplicateContainmentThreshold)
                {
                    isDuplicate = true;
                    break;
                }
                
                bool sameText = string.Equals(
                    candidate.SurfaceText,
                    accepted.SurfaceText,
                    StringComparison.Ordinal);
                
                if (sameText)
                {
                    double iou = GeometryHelper.IoU(
                        candidate.ScreenBoundingBox,
                        accepted.ScreenBoundingBox);

                    if (iou > DuplicateIoUThreshold)
                    {
                        isDuplicate = true;
                        break;
                    }
                }
            }

            if (!isDuplicate)
                result.Add(candidate);
        }

        return result;
    }

    /// <summary>
    /// Removes overlays whose bounding box is too small to be clickable.
    /// </summary>
    public static List<WordOverlay> RemoveTinyBoxes(IReadOnlyList<WordOverlay> overlays)
    {
        var result = new List<WordOverlay>(overlays.Count);
        foreach (var o in overlays)
        {
            if (o.ScreenBoundingBox.Width  >= MinDimension &&
                o.ScreenBoundingBox.Height >= MinDimension)
            {
                result.Add(o);
            }
        }
        return result;
    }
}
