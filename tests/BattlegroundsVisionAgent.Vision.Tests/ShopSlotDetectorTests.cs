using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Geometry;
using BattlegroundsVisionAgent.Vision.Recognition;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Tests;

public sealed class ShopSlotDetectorTests
{
    [Fact]
    public void Detect_ReturnsOnlyOccupiedCenteredSlots()
    {
        const int width = 1920;
        const int height = 1080;
        var region = new NormalizedRect(0.214, 0.273, 0.595, 0.187);
        var calibratedSlots = Enumerable.Range(0, 7)
            .Select(index => new NormalizedRect(0.221 + index * 0.085, 0.282, 0.071, 0.168))
            .ToArray();
        using var frame = new Mat(height, width, MatType.CV_8UC3, Scalar.All(0));
        var shop = region.ToPixels(width, height);
        foreach (var center in new[] { 198, 338, 478, 618, 758, 897 })
        {
            Cv2.Rectangle(frame,
                new Rect(shop.X + center - 15, shop.Y + 12, 30, 36),
                new Scalar(180, 0, 180),
                thickness: -1);
        }

        var slots = ShopSlotDetector.Detect(frame, region, calibratedSlots);

        Assert.Equal(6, slots.Count);
        var centers = slots.Select(slot => (slot.X + slot.Width / 2) * width).ToArray();
        var expected = new[] { 610, 750, 890, 1030, 1170, 1309 };
        Assert.All(centers.Zip(expected), pair => Assert.InRange(pair.First, pair.Second - 3, pair.Second + 3));
    }

    [Fact]
    public void Detect_FallsBackToCalibration_WhenNoBadgeIsVisible()
    {
        const int width = 1920;
        const int height = 1080;
        var region = new NormalizedRect(0.214, 0.273, 0.595, 0.187);
        var calibratedSlots = Enumerable.Range(0, 7)
            .Select(index => new NormalizedRect(0.221 + index * 0.085, 0.282, 0.071, 0.168))
            .ToArray();
        using var frame = new Mat(height, width, MatType.CV_8UC3, Scalar.All(0));

        var slots = ShopSlotDetector.Detect(frame, region, calibratedSlots);

        Assert.Equal(calibratedSlots, slots);
    }
}
