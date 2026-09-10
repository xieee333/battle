using BattlegroundsVisionAgent.Core.Domain;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Recognition;

public sealed record CardKindDetection(CardKind Kind, double Confidence)
{
    public static CardKindDetection Unknown() => new(CardKind.Unknown, 0);
}

/// <summary>
/// Separates an unlisted tavern spell from an unrecognized minion.  The
/// Chinese official catalog endpoint currently returns the minion pool used by
/// this app, while a spell can still occupy a shop slot.  A minion has two
/// stat orbs at the lower corners; a spell does not.
/// </summary>
public static class CardKindDetector
{
    public static CardKindDetection DetectShopCard(Mat image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Empty() || image.Width < 20 || image.Height < 20)
            return CardKindDetection.Unknown();

        using var gray = new Mat();
        if (image.Channels() == 1)
            image.CopyTo(gray);
        else
            Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        using var edges = new Mat();
        Cv2.Canny(gray, edges, 80, 160);
        var top = edges.Height * 63 / 100;
        var height = Math.Max(1, edges.Height * 31 / 100);
        var leftWidth = Math.Max(1, edges.Width * 42 / 100);
        var rightX = edges.Width * 58 / 100;
        var rightWidth = Math.Max(1, edges.Width - rightX);
        using var leftEdges = new Mat(edges, new Rect(0, top, leftWidth, Math.Min(height, edges.Height - top)));
        using var rightEdges = new Mat(edges, new Rect(rightX, top, rightWidth, Math.Min(height, edges.Height - top)));
        var edgeScore = (Cv2.CountNonZero(leftEdges) + Cv2.CountNonZero(rightEdges))
            / (double)(leftEdges.Width * leftEdges.Height + rightEdges.Width * rightEdges.Height);
        if (edgeScore >= 0.12)
        {
            var confidence = Math.Clamp(0.78 + Math.Min(0.17, (edgeScore - 0.12) * 1.8), 0, 0.95);
            return new CardKindDetection(CardKind.Minion, confidence);
        }

        // The spell slot still has a card frame and art, but its lower-corner
        // edge pattern is much weaker than a minion's two stat orbs.
        return edgeScore >= 0.018
            ? new CardKindDetection(CardKind.Spell, Math.Clamp(0.72 + edgeScore * 1.4, 0, 0.90))
            : CardKindDetection.Unknown();
    }

}
