using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Geometry;
using BattlegroundsVisionAgent.Vision.Recognition;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Tests;

public sealed class TemplateLayoutRecognizerTests
{
    [Fact]
    public void Recognize_FindsRequiredAnchorsAndBuildsVersionedSlots()
    {
        using var frame = new Mat(108, 192, MatType.CV_8UC1, Scalar.All(0));
        using var shop = CreateTemplate(1);
        using var hand = CreateTemplate(2);
        using var board = CreateTemplate(3);
        Place(frame, shop, 10, 10);
        Place(frame, hand, 10, 50);
        Place(frame, board, 110, 50);

        using var recognizer = new TemplateLayoutRecognizer(
            Profile(),
            new Dictionary<string, Mat>
            {
                ["shop"] = shop,
                ["hand"] = hand,
                ["board"] = board
            });

        var result = recognizer.Recognize(frame);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.LayoutVersion);
        Assert.Equal(1, result.Confidence, precision: 3);
        Assert.Equal(4, result.Slots.Count);
        Assert.Equal(2, result.Slots.Count(slot => slot.Zone == CardZone.Shop));
        Assert.Equal(1, result.Slots.Count(slot => slot.Zone == CardZone.Hand));
        Assert.Equal(1, result.Slots.Count(slot => slot.Zone == CardZone.Board));
    }

    [Fact]
    public void Recognize_ReturnsUnknownWhenARequiredAnchorIsMissing()
    {
        using var frame = new Mat(108, 192, MatType.CV_8UC1, Scalar.All(0));
        using var shop = CreateTemplate(1);
        using var hand = CreateTemplate(2);
        using var board = CreateTemplate(3);
        using var recognizer = new TemplateLayoutRecognizer(
            Profile(),
            new Dictionary<string, Mat>
            {
                ["shop"] = shop,
                ["hand"] = hand,
                ["board"] = board
            });

        var result = recognizer.Recognize(frame);

        Assert.False(result.IsSuccess);
        Assert.True(result.HasUnknownBlockingUi);
    }

    private static LayoutTemplateProfile Profile() => new(
        new LayoutRegions(
            new NormalizedRect(0.05, 0.05, 0.40, 0.25),
            new NormalizedRect(0.05, 0.40, 0.35, 0.25),
            new NormalizedRect(0.55, 0.40, 0.40, 0.25)),
        [
            new NormalizedRect(0.08, 0.08, 0.08, 0.12),
            new NormalizedRect(0.18, 0.08, 0.08, 0.12)
        ],
        [new NormalizedRect(0.08, 0.45, 0.08, 0.12)],
        [new NormalizedRect(0.58, 0.45, 0.08, 0.12)],
        [],
        new NormalizedRect(0.90, 0.02, 0.05, 0.05),
        new NormalizedRect(0.82, 0.02, 0.05, 0.05),
        10,
        7);

    private static Mat CreateTemplate(int variant)
    {
        var template = new Mat(7, 7, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(template, new Rect(1, 1, 5, 5), Scalar.All(255), 1);
        Cv2.Line(template, new Point(variant, 1), new Point(6 - variant, 5), Scalar.All(255), 1);
        return template;
    }

    private static void Place(Mat frame, Mat template, int x, int y)
    {
        using var destination = new Mat(frame, new Rect(x, y, template.Width, template.Height));
        template.CopyTo(destination);
    }
}
