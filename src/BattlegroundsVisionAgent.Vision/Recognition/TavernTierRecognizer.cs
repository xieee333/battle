using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Geometry;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Recognition;

public interface ITavernTierRecognizer
{
    DigitRecognition Recognize(Mat image, NormalizedRect bounds);
}

/// <summary>
/// Reads the current tavern tier from the star badge attached to the lower-left
/// of the hero portrait.  That badge is a star count, not the numeric upgrade
/// price displayed on the purple button to its right.
/// </summary>
public sealed class TavernTierRecognizer : ITavernTierRecognizer
{
    public DigitRecognition Recognize(Mat image, NormalizedRect bounds)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Empty() || bounds.Width <= 0 || bounds.Height <= 0)
            return new DigitRecognition(null, bounds, 0);

        var pixels = bounds.ToPixels(image.Width, image.Height);
        if (!pixels.IsWithin(image.Width, image.Height))
            return new DigitRecognition(null, bounds, 0);

        using var crop = new Mat(image, new Rect(pixels.X, pixels.Y, pixels.Width, pixels.Height));
        using var bgr = new Mat();
        using var hsv = new Mat();
        using var mask = new Mat();
        if (crop.Channels() == 1)
            Cv2.CvtColor(crop, bgr, ColorConversionCodes.GRAY2BGR);
        else
            crop.CopyTo(bgr);
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);

        // The stars are saturated gold while the purple shield and the hero
        // portrait are not.  Keep the mask inside the badge to ignore nearby
        // hero art and the green hover outline.
        Cv2.InRange(hsv, new Scalar(0, 35, 100), new Scalar(50, 255, 255), mask);
        using var roi = new Mat(mask,
            new Rect(
                Math.Clamp(mask.Width / 10, 0, Math.Max(0, mask.Width - 1)),
                Math.Clamp(mask.Height / 10, 0, Math.Max(0, mask.Height - 1)),
                Math.Max(1, mask.Width * 8 / 10),
                Math.Max(1, mask.Height * 8 / 10)));
        using var clean = roi.Clone();

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var components = Cv2.ConnectedComponentsWithStats(clean, labels, stats, centroids);
        var stars = 0;
        for (var component = 1; component < components; component++)
        {
            var area = stats.At<int>(component, (int)ConnectedComponentsTypes.Area);
            var y = stats.At<int>(component, (int)ConnectedComponentsTypes.Top);
            var width = stats.At<int>(component, (int)ConnectedComponentsTypes.Width);
            var height = stats.At<int>(component, (int)ConnectedComponentsTypes.Height);
            if (area is >= 70 and <= 220
                && y <= roi.Height * 85 / 100
                && width is >= 10 and <= 22
                && height is >= 10 and <= 22)
                stars++;
        }

        if (stars is < 1 or > 6)
            return new DigitRecognition(null, bounds, Math.Clamp(stars / 8d, 0, 0.65));
        return new DigitRecognition(stars, bounds, Math.Clamp(0.82 + stars * 0.02, 0, 0.94));
    }
}
