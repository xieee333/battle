using BattlegroundsVisionAgent.Core.Domain;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Recognition;

public sealed record GoldCoinRecognition(
    int? Value,
    double Confidence,
    bool IsVisible,
    IReadOnlyList<double> SlotScores)
{
    public bool IsKnown => Value.HasValue;

    public static GoldCoinRecognition NotVisible() => new(null, 0, false, []);
}

/// <summary>
/// Counts the available bright gold slots shown by the Battlegrounds UI. The
/// configured region is a maximum-capacity region: early turns may show only
/// three or a few slots, while later turns can show all ten. Bright coins are
/// counted; dark and missing slots are not. The recognizer deliberately returns
/// unknown when the whole bar is absent, which is different from a visible
/// 0-gold bar.
/// </summary>
public static class GoldCoinRecognizer
{
    private const int SlotCount = 10;
    private const double FilledThreshold = 0.08;
    private const double VisibleThreshold = 0.04;

    public static GoldCoinRecognition Recognize(Mat frame, NormalizedRect bounds)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Empty() || bounds.Width <= 0 || bounds.Height <= 0)
            return GoldCoinRecognition.NotVisible();

        using var bar = Crop(frame, bounds);
        if (bar.Empty() || bar.Width < SlotCount || bar.Height < 4)
            return GoldCoinRecognition.NotVisible();

        var scores = new double[SlotCount];
        for (var index = 0; index < SlotCount; index++)
        {
            var left = (int)Math.Round(index * bar.Width / (double)SlotCount);
            var right = (int)Math.Round((index + 1) * bar.Width / (double)SlotCount);
            var insetX = Math.Max(1, (right - left) / 10);
            var insetY = Math.Max(1, bar.Height / 10);
            var width = Math.Max(1, right - left - insetX * 2);
            var height = Math.Max(1, bar.Height - insetY * 2);
            using var slot = new Mat(bar, new Rect(left + insetX, insetY, width, height));
            scores[index] = GoldPixelRatio(slot);
        }

        var visibleScores = scores.Where(score => score >= VisibleThreshold).ToArray();
        if (visibleScores.Length == 0)
            return GoldCoinRecognition.NotVisible();

        var value = scores.Count(score => score >= FilledThreshold);
        var strongest = scores.Max();
        var weakestFilled = scores.Where(score => score >= FilledThreshold).DefaultIfEmpty(strongest).Min();
        var confidence = Math.Clamp(0.82 + Math.Min(0.15, Math.Max(0, weakestFilled - FilledThreshold)), 0, 0.97);
        return new GoldCoinRecognition(value, confidence, true, scores);
    }

    private static double GoldPixelRatio(Mat image)
    {
        using var hsv = new Mat();
        if (image.Channels() == 1)
            Cv2.CvtColor(image, hsv, ColorConversionCodes.GRAY2BGR);
        else
            image.CopyTo(hsv);
        using var converted = new Mat();
        Cv2.CvtColor(hsv, converted, ColorConversionCodes.BGR2HSV);
        using var mask = new Mat();
        Cv2.InRange(converted, new Scalar(5, 70, 90), new Scalar(45, 255, 255), mask);
        return Cv2.CountNonZero(mask) / (double)(mask.Width * mask.Height);
    }

    private static Mat Crop(Mat frame, NormalizedRect bounds)
    {
        var x = Math.Clamp((int)Math.Floor(bounds.X * frame.Width), 0, Math.Max(0, frame.Width - 1));
        var y = Math.Clamp((int)Math.Floor(bounds.Y * frame.Height), 0, Math.Max(0, frame.Height - 1));
        var width = Math.Min(Math.Max(1, (int)Math.Ceiling(bounds.Width * frame.Width)), frame.Width - x);
        var height = Math.Min(Math.Max(1, (int)Math.Ceiling(bounds.Height * frame.Height)), frame.Height - y);
        return new Mat(frame, new Rect(x, y, width, height)).Clone();
    }
}
