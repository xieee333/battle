using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Geometry;
using BattlegroundsVisionAgent.Vision.Recognition;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Tests;

public sealed class DynamicZoneRecognitionTests
{
    [Fact]
    public void TavernTierRecognizer_CountsStarsInsteadOfUpgradePrice()
    {
        using var image = new Mat(100, 100, MatType.CV_8UC3, new Scalar(25, 25, 25));
        var gold = new Scalar(0, 215, 255);
        foreach (var center in new[]
        {
            new Point(30, 30), new Point(50, 30),
            new Point(30, 50), new Point(50, 50)
        })
            Cv2.Circle(image, center, 7, gold, -1);

        var result = new TavernTierRecognizer().Recognize(
            image,
            new NormalizedRect(0.15, 0.15, 0.50, 0.50));

        Assert.True(result.IsKnown);
        Assert.Equal(4, result.Value);
    }

    [Fact]
    public void OccupancyDetector_DoesNotTreatAnEmptyBoardSlotAsACard()
    {
        using var empty = new Mat(180, 140, MatType.CV_8UC3, new Scalar(25, 25, 25));
        using var occupied = empty.Clone();
        Cv2.Circle(occupied, new Point(22, 140), 14, new Scalar(240, 240, 240), 3);
        Cv2.Circle(occupied, new Point(118, 140), 14, new Scalar(240, 240, 240), 3);

        var detector = new CardOccupancyDetector();

        Assert.False(detector.Detect(empty, CardZone.Board).IsOccupied);
        Assert.True(detector.Detect(occupied, CardZone.Board).IsOccupied);
    }

    [Fact]
    public void HandSlotDetector_RecoversOverlappedNineCardHand()
    {
        var root = FindRepositoryRoot();
        var profilePath = Path.Combine(root, "data", "vision", "profile.json");
        var screenshotPath = Path.Combine(root, "logs", "live-frames", "20260907-214645-774-Shopping.png");
        Assert.True(File.Exists(profilePath), profilePath);
        Assert.True(File.Exists(screenshotPath), screenshotPath);

        using var assets = VisionProfileAssets.Load(profilePath);
        using var frame = Cv2.ImRead(screenshotPath, ImreadModes.Color);
        var slots = CardZoneSlotDetector.DetectHand(frame, assets.Layout.Regions.Hand, assets.Layout.HandSlots);
        Assert.Equal(9, slots.Count);
    }

    [Fact]
    public void HandSlotDetector_DoesNotExpandVisibleFourCardHandToNineCandidates()
    {
        var root = FindRepositoryRoot();
        var profilePath = Path.Combine(root, "data", "vision", "profile.json");
        var screenshotPath = Path.Combine(root, "logs", "live-frames", "20260907-193900-058-Shopping.png");
        Assert.True(File.Exists(profilePath), profilePath);
        Assert.True(File.Exists(screenshotPath), screenshotPath);

        using var assets = VisionProfileAssets.Load(profilePath);
        using var frame = Cv2.ImRead(screenshotPath, ImreadModes.Color);
        var slots = CardZoneSlotDetector.DetectHand(frame, assets.Layout.Regions.Hand, assets.Layout.HandSlots);
        Assert.Equal(4, slots.Count);
    }

    [Fact]
    public void HandSlotDetector_DoesNotTreatFullyExposedFourCardFanAsFullHand()
    {
        var root = FindRepositoryRoot();
        var profilePath = Path.Combine(root, "data", "vision", "profile.json");
        var screenshotPath = Path.Combine(root, "logs", "live-frames", "20260907-214047-733-Shopping.png");
        Assert.True(File.Exists(profilePath), profilePath);
        Assert.True(File.Exists(screenshotPath), screenshotPath);

        using var assets = VisionProfileAssets.Load(profilePath);
        using var frame = Cv2.ImRead(screenshotPath, ImreadModes.Color);
        var slots = CardZoneSlotDetector.DetectHand(frame, assets.Layout.Regions.Hand, assets.Layout.HandSlots);
        Assert.Equal(4, slots.Count);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BattlegroundsVisionAgent.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

}
