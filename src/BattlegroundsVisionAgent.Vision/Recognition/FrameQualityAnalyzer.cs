using BattlegroundsVisionAgent.Vision.Catalog;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Recognition;

public enum FrameQuality
{
    Good,
    Review,
    Garbage
}

public static class FrameQualityPolicy
{
    public const int MinimumWidth = 32;
    public const int MinimumHeight = 32;
    public const double BrightRatioGarbageThreshold = 0.95;
    public const double DarkRatioGarbageThreshold = 0.50;
    public const double BrightGarbageMaximumStandardDeviation = 12;
    public const double DarkGarbageMaximumStandardDeviation = 20;
    public const double ReviewBrightnessBoundary = 24;
    public const double ReviewEdgeRatioBoundary = 0.002;
}

public sealed record FrameQualityMetrics(
    int Width,
    int Height,
    double BrightnessMean,
    double BrightnessStdDev,
    double BrightRatio,
    double DarkRatio,
    double EdgeRatio,
    ulong PerceptualHash);

public sealed record FrameQualityResult(
    string? SourcePath,
    FrameQuality Quality,
    string Reason,
    FrameQualityMetrics Metrics);

public static class FrameQualityAnalyzer
{
    private const int SampleWidth = 32;
    private const int SampleHeight = 18;

    public static FrameQualityResult Analyze(Mat image, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Empty())
            return DecodeFailed(sourcePath, 0, 0);

        if (image.Width < FrameQualityPolicy.MinimumWidth || image.Height < FrameQualityPolicy.MinimumHeight)
            return Result(sourcePath, FrameQuality.Garbage, "too-small", EmptyMetrics(image.Width, image.Height));

        using var gray = new Mat();
        if (image.Channels() == 1)
            image.CopyTo(gray);
        else
            Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);

        using var sample = new Mat();
        Cv2.Resize(gray, sample, new Size(SampleWidth, SampleHeight), 0, 0, InterpolationFlags.Area);
        Cv2.MeanStdDev(sample, out var mean, out var standardDeviation);

        var samplePixels = sample.Rows * sample.Cols;
        using var bright = new Mat();
        using var dark = new Mat();
        Cv2.InRange(sample, new Scalar(245), new Scalar(255), bright);
        Cv2.InRange(sample, new Scalar(0), new Scalar(16), dark);

        using var edges = new Mat();
        Cv2.Canny(sample, edges, 50, 150);
        var metrics = new FrameQualityMetrics(
            image.Width,
            image.Height,
            mean.Val0,
            standardDeviation.Val0,
            Cv2.CountNonZero(bright) / (double)samplePixels,
            Cv2.CountNonZero(dark) / (double)samplePixels,
            Cv2.CountNonZero(edges) / (double)samplePixels,
            PerceptualHash.Create(gray));

        if (metrics.BrightRatio >= FrameQualityPolicy.BrightRatioGarbageThreshold
            && metrics.BrightnessStdDev < FrameQualityPolicy.BrightGarbageMaximumStandardDeviation)
            return Result(sourcePath, FrameQuality.Garbage, "mostly-white", metrics);

        if (metrics.DarkRatio >= FrameQualityPolicy.DarkRatioGarbageThreshold
            && metrics.BrightnessStdDev < FrameQualityPolicy.DarkGarbageMaximumStandardDeviation)
            return Result(sourcePath, FrameQuality.Garbage, "mostly-dark", metrics);

        if (metrics.BrightnessMean <= FrameQualityPolicy.ReviewBrightnessBoundary
            || metrics.BrightnessMean >= 255 - FrameQualityPolicy.ReviewBrightnessBoundary)
            return Result(sourcePath, FrameQuality.Review, "extreme-brightness", metrics);

        if (metrics.EdgeRatio < FrameQualityPolicy.ReviewEdgeRatioBoundary)
            return Result(sourcePath, FrameQuality.Review, "low-texture", metrics);

        return Result(sourcePath, FrameQuality.Good, "ok", metrics);
    }

    public static FrameQualityResult AnalyzeFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using var image = Cv2.ImRead(path, ImreadModes.Color);
            return image.Empty()
                ? DecodeFailed(path, 0, 0)
                : Analyze(image, path);
        }
        catch (Exception) when (File.Exists(path))
        {
            return DecodeFailed(path, 0, 0);
        }
        catch (Exception)
        {
            return DecodeFailed(path, 0, 0);
        }
    }

    private static FrameQualityResult DecodeFailed(string? sourcePath, int width, int height) =>
        Result(sourcePath, FrameQuality.Garbage, "decode-failed", EmptyMetrics(width, height));

    private static FrameQualityMetrics EmptyMetrics(int width, int height) =>
        new(width, height, 0, 0, 0, 0, 0, 0);

    private static FrameQualityResult Result(
        string? sourcePath,
        FrameQuality quality,
        string reason,
        FrameQualityMetrics metrics) =>
        new(sourcePath, quality, reason, metrics);
}
