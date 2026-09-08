using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Geometry;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Recognition;

/// <summary>
/// Finds the visible shop cards from the purple tier badges rendered above them.
/// The game centers the shop row when the number of cards changes, so a fixed
/// seven-column split can cut the first card in half and classify the wood table
/// as a card. This detector returns only confidently occupied slots and lets the
/// caller fall back to the calibrated layout when the badges are unavailable.
/// </summary>
public static class ShopSlotDetector
{
    private const int MinimumComponentArea = 250;
    private const int MinimumComponentWidth = 20;
    private const int MaximumComponentWidth = 65;
    private const int MinimumComponentHeight = 24;
    private const int MaximumComponentHeight = 70;
    private const double MaximumBadgeCenterYRatio = 0.36;

    public static IReadOnlyList<NormalizedRect> Detect(
        Mat frame,
        NormalizedRect shopRegion,
        IReadOnlyList<NormalizedRect> calibratedSlots)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(calibratedSlots);
        if (frame.Empty() || calibratedSlots.Count == 0)
            return calibratedSlots;

        PixelRect region;
        try
        {
            region = shopRegion.ToPixels(frame.Width, frame.Height);
        }
        catch (ArgumentOutOfRangeException)
        {
            return calibratedSlots;
        }

        if (!region.IsWithin(frame.Width, frame.Height) || region.Width < 1 || region.Height < 1)
            return calibratedSlots;

        using var shop = new Mat(frame, new Rect(region.X, region.Y, region.Width, region.Height));
        using var color = new Mat();
        using var hsv = new Mat();
        using var mask = new Mat();
        if (shop.Channels() == 1)
            Cv2.CvtColor(shop, color, ColorConversionCodes.GRAY2BGR);
        else
            shop.CopyTo(color);
        Cv2.CvtColor(color, hsv, ColorConversionCodes.BGR2HSV);
        // OpenCV hue is 0..179. The tier badge is magenta/purple in both normal
        // and golden cards; use a broad hue range but require strong saturation.
        Cv2.InRange(hsv, new Scalar(130, 100, 60), new Scalar(179, 255, 255), mask);

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var componentCount = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);
        var centers = new List<double>();
        for (var component = 1; component < componentCount; component++)
        {
            var area = stats.At<int>(component, (int)ConnectedComponentsTypes.Area);
            var width = stats.At<int>(component, (int)ConnectedComponentsTypes.Width);
            var height = stats.At<int>(component, (int)ConnectedComponentsTypes.Height);
            var centerX = centroids.At<double>(component, 0);
            var centerY = centroids.At<double>(component, 1);
            if (area < MinimumComponentArea
                || width is < MinimumComponentWidth or > MaximumComponentWidth
                || height is < MinimumComponentHeight or > MaximumComponentHeight
                || centerY > region.Height * MaximumBadgeCenterYRatio)
                continue;

            centers.Add(centerX + region.X);
        }

        var distinctCenters = centers
            .OrderBy(center => center)
            .Aggregate(new List<double>(), (result, center) =>
            {
                if (result.Count == 0 || center - result[^1] >= MinimumSlotCenterDistance(calibratedSlots, frame.Width))
                    result.Add(center);
                else
                    result[^1] = (result[^1] + center) / 2;
                return result;
            });

        if (distinctCenters.Count == 0 || distinctCenters.Count > calibratedSlots.Count)
            return calibratedSlots;

        var calibratedPixels = calibratedSlots
            .Select(slot => slot.ToPixels(frame.Width, frame.Height))
            .ToArray();
        var slotWidth = (int)Math.Round(calibratedPixels.Average(slot => slot.Width));
        var slotHeight = (int)Math.Round(calibratedPixels.Average(slot => slot.Height));
        var slotY = calibratedPixels.Min(slot => slot.Y);
        var slots = new List<NormalizedRect>(distinctCenters.Count);
        foreach (var center in distinctCenters)
        {
            var x = (int)Math.Round(center - slotWidth / 2d);
            x = Math.Clamp(x, region.X, region.X + region.Width - slotWidth);
            var y = Math.Clamp(slotY, region.Y, region.Y + region.Height - slotHeight);
            slots.Add(new NormalizedRect(
                (double)x / frame.Width,
                (double)y / frame.Height,
                (double)slotWidth / frame.Width,
                (double)slotHeight / frame.Height));
        }

        return slots;
    }

    private static double MinimumSlotCenterDistance(IReadOnlyList<NormalizedRect> calibratedSlots, int frameWidth)
    {
        if (calibratedSlots.Count < 2)
            return frameWidth * 0.04;

        var centers = calibratedSlots
            .Select(slot => (slot.X + slot.Width / 2) * frameWidth)
            .OrderBy(center => center)
            .ToArray();
        var distance = centers.Zip(centers.Skip(1), (left, right) => right - left).DefaultIfEmpty(frameWidth * 0.04).Min();
        return Math.Max(frameWidth * 0.04, distance * 0.45);
    }
}
