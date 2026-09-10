using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Recognition;

namespace BattlegroundsVisionAgent.Vision.Tests;

public sealed class ScreenshotRecognitionServiceTests
{
    [Fact]
    public void RecognizeFile_ProducesStructuredZonesFromRealShoppingScreenshot()
    {
        var root = FindRepositoryRoot();
        var profile = Path.Combine(root, "data", "vision", "profile.json");
        var catalog = Path.Combine(root, "data", "catalog", "catalog.db");
        var screenshot = Path.Combine(root, "logs", "live-frames", "20260907-193405-455-Shopping.png");
        Assert.True(File.Exists(profile), profile);
        Assert.True(File.Exists(catalog), catalog);
        Assert.True(File.Exists(screenshot), screenshot);

        using var service = ScreenshotRecognitionService.Load(profile, catalog);
        var report = service.RecognizeFile(screenshot);

        Assert.Equal(FrameQuality.Good, report.Quality);
        Assert.Equal(GamePhase.Shopping, report.Scene.GamePhase);
        Assert.NotEmpty(report.ForZone(CardZone.Shop));
        Assert.NotEmpty(report.ForZone(CardZone.Board));
        Assert.NotEmpty(report.ForZone(CardZone.Hand));
        Assert.Contains("商店", report.ToText());
        Assert.Contains("\"Board\"", report.ToJson());
    }

    [Fact]
    public void RecognizeFile_StopsBeforeLayoutWhenScreenshotIsGarbage()
    {
        var root = FindRepositoryRoot();
        using var service = ScreenshotRecognitionService.Load(
            Path.Combine(root, "data", "vision", "profile.json"),
            Path.Combine(root, "data", "catalog", "catalog.db"));

        var report = service.RecognizeFile(Path.Combine(root, "logs", "live-frames", "20260907-203423-650-Unknown.png"));

        Assert.Equal(FrameQuality.Garbage, report.Quality);
        Assert.Empty(report.Cards);
        Assert.False(report.IsActionable);
        Assert.Contains("frame-quality-garbage", report.BlockingReasons);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BattlegroundsVisionAgent.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
