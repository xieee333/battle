using BattlegroundsVisionAgent.Core.Domain;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Recognition;

public sealed record CardOccupancyDetection(bool IsOccupied, double Confidence);

public interface ICardOccupancyDetector
{
    CardOccupancyDetection Detect(Mat cardImage, CardZone zone);
}

/// <summary>
/// Filters calibrated empty slots.  The board uses the two lower stat-orb
/// corners; unlike a full-region color ratio this ignores the blue animation
/// glow outside the last board slot.  Hand slots are dynamically built from
/// cost-circle positions, so a non-empty crop is enough there.
/// </summary>
public sealed class CardOccupancyDetector : ICardOccupancyDetector
{
    public CardOccupancyDetection Detect(Mat cardImage, CardZone zone)
    {
        ArgumentNullException.ThrowIfNull(cardImage);
        if (cardImage.Empty())
            return new CardOccupancyDetection(false, 0);
        if (zone is CardZone.Shop or CardZone.Discover)
            return new CardOccupancyDetection(true, 0.95);
        if (zone == CardZone.Hand)
            return DetectHand(cardImage);
        return DetectBoard(cardImage);
    }

    private static CardOccupancyDetection DetectBoard(Mat image)
    {
        var score = LowerCornerEdgeScore(image);
        var occupied = score >= 0.045;
        return new CardOccupancyDetection(occupied, Math.Clamp(0.75 + score * 1.5, 0, 0.97));
    }

    private static CardOccupancyDetection DetectHand(Mat image)
    {
        using var gray = ToGray(image);
        using var edges = new Mat();
        Cv2.Canny(gray, edges, 80, 160);
        var score = Cv2.CountNonZero(edges) / (double)(edges.Width * edges.Height);
        var occupied = score >= 0.012;
        return new CardOccupancyDetection(occupied, Math.Clamp(0.72 + score * 1.4, 0, 0.94));
    }

    private static double LowerCornerEdgeScore(Mat image)
    {
        using var gray = ToGray(image);
        using var edges = new Mat();
        Cv2.Canny(gray, edges, 80, 160);
        var top = edges.Height * 63 / 100;
        var height = Math.Max(1, edges.Height * 31 / 100);
        var leftWidth = Math.Max(1, edges.Width * 42 / 100);
        var rightX = edges.Width * 58 / 100;
        var rightWidth = Math.Max(1, edges.Width - rightX);
        using var left = new Mat(edges, new Rect(0, top, leftWidth, Math.Min(height, edges.Height - top)));
        using var right = new Mat(edges, new Rect(rightX, top, rightWidth, Math.Min(height, edges.Height - top)));
        return (Cv2.CountNonZero(left) + Cv2.CountNonZero(right))
            / (double)(left.Width * left.Height + right.Width * right.Height);
    }

    private static Mat ToGray(Mat image)
    {
        var gray = new Mat();
        if (image.Channels() == 1)
            image.CopyTo(gray);
        else
            Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        return gray;
    }
}
