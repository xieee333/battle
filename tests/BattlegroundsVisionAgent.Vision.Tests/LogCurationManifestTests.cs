using BattlegroundsVisionAgent.Vision.Recognition;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Tests;

public sealed class LogCurationManifestTests
{
    [Fact]
    public void Scan_ParsesSceneLabelsAndBlocksNonShoppingScenes()
    {
        var root = CreateTempDirectory("log-scan");
        try
        {
            WriteImage(root, "live-frames/20260907-200000-000-Shopping.png", CreateTexturedImage(0));
            WriteImage(root, "live-frames/20260907-200001-000-Discover.png", CreateTexturedImage(10));
            WriteImage(root, "live-frames/20260907-200002-000-Combat.png", CreateTexturedImage(20));
            WriteImage(root, "live-frames/20260907-200003-000-Unknown.png", CreateTexturedImage(30));

            var manifest = LogCurationScanner.Scan(root, "abc123");

            Assert.Equal("abc123", manifest.SourceCommit);
            Assert.Equal(4, manifest.Samples.Count);
            var shopping = Assert.Single(manifest.Samples, sample => sample.SourceLabel == "Shopping");
            var combat = Assert.Single(manifest.Samples, sample => sample.SourceLabel == "Combat");
            var discover = Assert.Single(manifest.Samples, sample => sample.SourceLabel == "Discover");
            var unknown = Assert.Single(manifest.Samples, sample => sample.SourceLabel == "Unknown");
            Assert.True(shopping.ExpectedActionable);
            Assert.False(combat.ExpectedActionable);
            Assert.False(discover.ExpectedActionable);
            Assert.False(unknown.ExpectedActionable);
            Assert.All(manifest.Samples, sample => Assert.True(sample.Include));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void Scan_MarksWhiteAndDuplicateFramesAsExcluded()
    {
        var root = CreateTempDirectory("log-quality");
        try
        {
            WriteImage(root, "live-frames/20260907-200000-000-Shopping.png", CreateTexturedImage());
            WriteImage(root, "live-frames/20260907-200001-000-Shopping.png", CreateTexturedImage());
            using var white = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.All(255));
            WriteImage(root, "live-frames/20260907-200002-000-Unknown.png", white);

            var manifest = LogCurationScanner.Scan(root, "abc123");

            var duplicate = Assert.Single(manifest.Samples, sample => sample.Path.EndsWith("200001-000-Shopping.png", StringComparison.Ordinal));
            var garbage = Assert.Single(manifest.Samples, sample => sample.Path.EndsWith("200002-000-Unknown.png", StringComparison.Ordinal));
            Assert.False(duplicate.Include);
            Assert.Equal("duplicate", duplicate.Reason);
            Assert.False(garbage.Include);
            Assert.Equal(FrameQuality.Garbage, garbage.Quality);
            Assert.Equal("mostly-white", garbage.Reason);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void Serialize_IsDeterministicAndUsesStablePathOrder()
    {
        var root = CreateTempDirectory("log-json");
        try
        {
            WriteImage(root, "z/20260907-200002-000-Combat.png", CreateTexturedImage());
            WriteImage(root, "a/20260907-200001-000-Shopping.png", CreateTexturedImage());
            var manifest = LogCurationScanner.Scan(root, "abc123");

            var first = manifest.ToJson();
            var second = LogCurationScanner.Scan(root, "abc123").ToJson();

            Assert.Equal(first, second);
            Assert.True(first.IndexOf("a/20260907-200001-000-Shopping.png", StringComparison.Ordinal)
                < first.IndexOf("z/20260907-200002-000-Combat.png", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void ReportFormatter_ContainsSceneQualityReasonsAndStableSamples()
    {
        var metrics = new FrameQualityMetrics(1920, 1080, 50, 20, 0, 0, 0.1, 1);
        var manifest = new LogCurationManifest(
            1,
            "abc123",
            1,
            [
                new LogCurationSample("z.png", "Combat", "Combat", FrameQuality.Good, true, false,
                    "scene-not-actionable", metrics, "ok"),
                new LogCurationSample("a.png", "Unknown", "Unknown", FrameQuality.Garbage, false, false,
                    "frame-quality-garbage", metrics, "mostly-white"),
                new LogCurationSample("m.png", "Shopping", "Shopping", FrameQuality.Review, true, false,
                    "frame-quality-review", metrics, "low-texture")
            ]);

        var report = LogCurationReportFormatter.Format(manifest);

        Assert.Contains("Shopping: 1", report, StringComparison.Ordinal);
        Assert.Contains("Discover: 0", report, StringComparison.Ordinal);
        Assert.Contains("Combat: 1", report, StringComparison.Ordinal);
        Assert.Contains("Unknown: 1", report, StringComparison.Ordinal);
        Assert.Contains("Garbage: 1", report, StringComparison.Ordinal);
        Assert.Contains("mostly-white: 1", report, StringComparison.Ordinal);
        Assert.Contains("待复核: 1", report, StringComparison.Ordinal);
        Assert.True(report.IndexOf("a.png", StringComparison.Ordinal) < report.IndexOf("z.png", StringComparison.Ordinal));
    }

    private static Mat CreateTexturedImage(byte tint = 0)
    {
        var image = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.All(50 + tint));
        var height = image.Height;
        var width = image.Width;
        for (var y = 0; y < height; y += 120)
        {
            for (var x = 0; x < width; x += 120)
            {
                if ((x / 120 + y / 120) % 2 == 0)
                    Cv2.Rectangle(image, new Rect(x, y, Math.Min(120, width - x), Math.Min(120, height - y)),
                        new Scalar(180 - tint, 120 + tint, 70 + tint), thickness: -1);
            }
        }
        Cv2.Circle(image, new Point(120 + tint * 7, 120 + tint * 5), 24 + tint, new Scalar(240, 240, 240), thickness: -1);
        return image;
    }

    private static void WriteImage(string root, string relativePath, Mat image)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Cv2.ImWrite(path, image);
        image.Dispose();
    }

    private static string CreateTempDirectory(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteDirectory(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
