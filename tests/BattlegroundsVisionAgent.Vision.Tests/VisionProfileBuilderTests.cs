using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Geometry;
using BattlegroundsVisionAgent.Vision.Recognition;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Tests;

public sealed class VisionProfileBuilderTests
{
    [Fact]
    public void BuildShoppingProfile_WritesRunnableLayoutAndShoppingSceneSample()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vision-profile-build-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            using var frame = CreateScreenshot();
            var result = VisionProfileBuilder.BuildShoppingProfile(frame, Calibration(), root);

            Assert.True(File.Exists(result.ProfilePath));
            Assert.Equal(7, result.ShopSlotCount);
            Assert.Equal(10, result.HandSlotCount);
            Assert.Equal(7, result.BoardSlotCount);
            Assert.True(result.HasGoldRegion);
            Assert.True(result.HasTierRegion);
            Assert.True(File.Exists(Path.Combine(root, "anchors", "shop.png")));
            Assert.True(File.Exists(Path.Combine(root, "anchors", "hand.png")));
            Assert.True(File.Exists(Path.Combine(root, "anchors", "board.png")));
            Assert.True(File.Exists(Path.Combine(root, "scenes", "shopping.png")));

            using var assets = VisionProfileAssets.Load(result.ProfilePath);
            using var layoutRecognizer = new TemplateLayoutRecognizer(assets.Layout, assets.AnchorTemplates);
            var layout = layoutRecognizer.Recognize(frame);
            Assert.True(layout.IsSuccess);
            Assert.Equal(24, layout.Slots.Count);
            Assert.Equal(GamePhase.Shopping, new SceneRecognizer(assets.SceneTemplates).Recognize(frame).GamePhase);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildShoppingProfile_RejectsMissingRequiredZone()
    {
        using var frame = CreateScreenshot();
        var regions = Calibration().Regions;
        regions.Remove("board");

        Assert.Throws<InvalidDataException>(() =>
            VisionProfileBuilder.BuildShoppingProfile(
                frame,
                new RegionCalibration(1, frame.Width, frame.Height, regions),
                Path.Combine(Path.GetTempPath(), $"vision-profile-build-{Guid.NewGuid():N}")));
    }

    private static RegionCalibration Calibration() => new(
        1,
        1920,
        1080,
        new Dictionary<string, CalibrationRect>
        {
            ["shop"] = new(0.10, 0.10, 0.80, 0.25),
            ["board"] = new(0.10, 0.42, 0.80, 0.22),
            ["hand"] = new(0.10, 0.72, 0.80, 0.12),
            ["gold"] = new(0.02, 0.02, 0.06, 0.05),
            ["tier"] = new(0.10, 0.02, 0.06, 0.05)
        });

    private static Mat CreateScreenshot()
    {
        var frame = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(18, 24, 38));
        Cv2.Rectangle(frame, new Rect(192, 108, 1536, 270), new Scalar(40, 80, 140), 4);
        Cv2.Rectangle(frame, new Rect(192, 453, 1536, 237), new Scalar(80, 140, 100), 4);
        Cv2.Rectangle(frame, new Rect(192, 777, 1536, 129), new Scalar(140, 80, 80), 4);
        Cv2.Line(frame, new Point(205, 120), new Point(480, 320), new Scalar(230, 220, 80), 5);
        Cv2.Line(frame, new Point(250, 790), new Point(610, 880), new Scalar(80, 220, 230), 5);
        return frame;
    }
}
