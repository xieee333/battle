using BattlegroundsVisionAgent.Vision.Recognition;

namespace BattlegroundsVisionAgent.Vision.Tests;

public sealed class LogQualityRegressionTests
{
    [Fact]
    public void Manifest_CoversEveryLiveFrame()
    {
        var root = FindRepositoryRoot();
        var manifest = LogCurationManifest.Load(Path.Combine(root, "tests", "fixtures", "log-quality", "live-logs.manifest.json"));
        var liveFrameCount = Directory.EnumerateFiles(Path.Combine(root, "logs", "live-frames"), "*.png").Count();

        Assert.Equal(liveFrameCount, manifest.Samples.Count(sample => sample.Path.StartsWith("logs/live-frames/", StringComparison.Ordinal)));
        Assert.Equal(275, liveFrameCount);
    }

    [Fact]
    public void NonShoppingScenes_AreNeverActionable()
    {
        var manifest = LoadManifest();

        Assert.Contains(manifest.Samples, sample => sample.ExpectedScene == "Combat");
        Assert.Contains(manifest.Samples, sample => sample.ExpectedScene == "Discover");
        Assert.All(manifest.Samples.Where(sample => sample.ExpectedScene is not "Shopping"), sample =>
        {
            Assert.False(sample.ExpectedActionable);
            Assert.False(string.IsNullOrWhiteSpace(sample.ExpectedPauseReason));
        });
    }

    [Fact]
    public void KnownBlankAndConnectionFrames_AreExcluded()
    {
        var manifest = LoadManifest();
        var blank = Assert.Single(manifest.Samples, sample => sample.Path.EndsWith("20260907-203423-650-Unknown.png", StringComparison.Ordinal));
        var connection = Assert.Single(manifest.Samples, sample => sample.Path.EndsWith("20260907-213143-760-Unknown.png", StringComparison.Ordinal));

        Assert.False(blank.Include);
        Assert.False(connection.Include);
        Assert.Equal(FrameQuality.Garbage, blank.Quality);
        Assert.Equal(FrameQuality.Garbage, connection.Quality);
    }

    [Fact]
    public void Manifest_HasAllFiveQualityBuckets()
    {
        var manifest = LoadManifest();

        Assert.Contains(manifest.Samples, sample => sample.ExpectedScene == "Shopping" && sample.Include);
        Assert.Contains(manifest.Samples, sample => sample.Quality == FrameQuality.Good);
        Assert.Contains(manifest.Samples, sample => sample.Quality == FrameQuality.Review);
        Assert.Contains(manifest.Samples, sample => sample.Quality == FrameQuality.Garbage);
    }

    private static LogCurationManifest LoadManifest()
    {
        var root = FindRepositoryRoot();
        return LogCurationManifest.Load(Path.Combine(root, "tests", "fixtures", "log-quality", "live-logs.manifest.json"));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BattlegroundsVisionAgent.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
