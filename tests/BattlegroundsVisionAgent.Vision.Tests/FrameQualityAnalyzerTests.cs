using BattlegroundsVisionAgent.Vision.Recognition;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Tests;

public sealed class FrameQualityAnalyzerTests
{
    [Fact]
    public void Analyze_MostlyWhiteFrame_IsGarbage()
    {
        using var image = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.All(255));

        var result = FrameQualityAnalyzer.Analyze(image);

        Assert.Equal(FrameQuality.Garbage, result.Quality);
        Assert.Equal("mostly-white", result.Reason);
        Assert.True(result.Metrics.BrightRatio >= FrameQualityPolicy.BrightRatioGarbageThreshold);
    }

    [Fact]
    public void Analyze_DarkTransitionFrame_IsGarbage()
    {
        using var image = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.All(8));
        Cv2.Rectangle(image, new Rect(700, 450, 500, 120), new Scalar(45, 45, 45), thickness: -1);

        var result = FrameQualityAnalyzer.Analyze(image);

        Assert.Equal(FrameQuality.Garbage, result.Quality);
        Assert.Equal("mostly-dark", result.Reason);
        Assert.True(result.Metrics.DarkRatio >= FrameQualityPolicy.DarkRatioGarbageThreshold);
    }

    [Fact]
    public void Analyze_TexturedFrame_IsGoodAndHasStableMetrics()
    {
        using var image = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.All(50));
        var height = image.Height;
        var width = image.Width;
        for (var y = 0; y < height; y += 120)
        {
            for (var x = 0; x < width; x += 120)
            {
                if ((x / 120 + y / 120) % 2 == 0)
                    Cv2.Rectangle(image, new Rect(x, y, Math.Min(120, width - x), Math.Min(120, height - y)),
                        new Scalar(180, 120, 70), thickness: -1);
            }
        }

        var first = FrameQualityAnalyzer.Analyze(image);
        var second = FrameQualityAnalyzer.Analyze(image);

        Assert.Equal(FrameQuality.Good, first.Quality);
        Assert.Equal(first.Metrics, second.Metrics);
        Assert.True(first.Metrics.EdgeRatio > 0);
        Assert.InRange(first.Metrics.BrightnessMean, 0, 255);
    }

    [Fact]
    public void AnalyzeFile_MissingFile_IsDecodeFailedGarbage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-frame-{Guid.NewGuid():N}.png");

        var result = FrameQualityAnalyzer.AnalyzeFile(path);

        Assert.Equal(FrameQuality.Garbage, result.Quality);
        Assert.Equal("decode-failed", result.Reason);
        Assert.Equal(path, result.SourcePath);
    }

    [Fact]
    public void Analyze_TooSmallImage_IsGarbage()
    {
        using var image = new Mat(16, 16, MatType.CV_8UC1, Scalar.All(120));

        var result = FrameQualityAnalyzer.Analyze(image);

        Assert.Equal(FrameQuality.Garbage, result.Quality);
        Assert.Equal("too-small", result.Reason);
    }
}
