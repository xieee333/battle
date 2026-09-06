using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Recognition;
using BattlegroundsVisionAgent.Vision.Catalog;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Tests;

public sealed class SnapshotRecognizerTests
{
    [Fact]
    public void Recognize_PreservesUnknownHandSlotAndBlocksActions()
    {
        using var frame = new Mat(100, 100, MatType.CV_8UC1, Scalar.All(40));
        var bounds = new NormalizedRect(0, 0, 1, 1);
        var recognizer = new SnapshotRecognizer(
            new StubLayoutRecognizer(LayoutRecognition.Succeeded(1, 1, [new CardSlot(CardZone.Hand, 0, bounds)], bounds, bounds, 1, 7)),
            new StubCardMatcher(CardMatch.Unknown(0.5)), new StubDigitRecognizer(1, 1, 1),
            new StubSceneRecognizer(new SceneRecognition(GamePhase.Shopping, 1)));

        var snapshot = recognizer.Recognize(frame, DateTimeOffset.UnixEpoch).Snapshot;

        Assert.Single(snapshot.Hand);
        Assert.Equal("UNKNOWN", snapshot.Hand[0].CardId);
        Assert.False(snapshot.IsActionable);
    }

    [Fact]
    public void Recognize_DoesNotBlockActionsForLegitimateEmptyHand()
    {
        using var frame = new Mat(100, 100, MatType.CV_8UC1, Scalar.All(40));
        var bounds = new NormalizedRect(0, 0, 1, 1);
        var recognizer = new SnapshotRecognizer(
            new StubLayoutRecognizer(LayoutRecognition.Succeeded(1, 1, [], bounds, bounds, 1, 7)),
            new StubCardMatcher(CardMatch.Unknown()), new StubDigitRecognizer(1, 1, 1),
            new StubSceneRecognizer(new SceneRecognition(GamePhase.Shopping, 1)));

        Assert.True(recognizer.Recognize(frame, DateTimeOffset.UnixEpoch).Snapshot.IsActionable);
    }
    [Fact]
    public void TemplateRecognizers_ReturnKnownValuesForExactSyntheticTemplates()
    {
        using var image = new Mat(100, 100, MatType.CV_8UC1, Scalar.All(30));
        Cv2.Circle(image, new Point(50, 50), 20, Scalar.All(200), 3);
        var bounds = new NormalizedRect(0, 0, 1, 1);
        var hash = PerceptualHash.Create(image);

        var digit = new DigitRecognizer([new DigitTemplate(8, hash)]).Recognize(image, bounds, "gold");
        var scene = new SceneRecognizer([new SceneTemplate(GamePhase.Shopping, hash)]).Recognize(image);

        Assert.Equal(8, digit.Value);
        Assert.Equal(GamePhase.Shopping, scene.GamePhase);
        Assert.Equal(1, digit.Confidence);
        Assert.Equal(1, scene.Confidence);
    }

    [Fact]
    public void SceneRecognizer_ToleratesSmallAnimatedOverlayOnSameScene()
    {
        using var template = new Mat(160, 240, MatType.CV_8UC1, Scalar.All(30));
        Cv2.Rectangle(template, new Rect(20, 20, 200, 110), Scalar.All(150), 4);
        Cv2.Line(template, new Point(40, 140), new Point(210, 40), Scalar.All(220), 3);
        using var liveFrame = template.Clone();
        Cv2.Rectangle(liveFrame, new Rect(185, 0, 55, 24), Scalar.All(255), -1);

        var scene = new SceneRecognizer([
            new SceneTemplate(GamePhase.Shopping, PerceptualHash.Create(template))
        ]).Recognize(liveFrame);

        Assert.True(scene.GamePhase == GamePhase.Shopping, $"Scene confidence was {scene.Confidence:F3}.");
        Assert.True(scene.Confidence >= SceneRecognizer.MinimumConfidence);
    }

    [Fact]
    public void SceneRecognizer_RejectsSubstantiallyDifferentScene()
    {
        using var template = new Mat(160, 240, MatType.CV_8UC1, Scalar.All(30));
        Cv2.Rectangle(template, new Rect(20, 20, 200, 110), Scalar.All(150), 4);
        using var differentFrame = new Mat(160, 240, MatType.CV_8UC1, Scalar.All(220));
        Cv2.Circle(differentFrame, new Point(120, 80), 55, Scalar.All(20), -1);

        var scene = new SceneRecognizer([
            new SceneTemplate(GamePhase.Shopping, PerceptualHash.Create(template))
        ]).Recognize(differentFrame);

        Assert.Equal(GamePhase.Unknown, scene.GamePhase);
        Assert.True(scene.Confidence < SceneRecognizer.MinimumConfidence);
    }

    [Fact]
    public void Recognize_AggregatesInjectedResultsIntoShoppingSnapshot()
    {
        using var frame = new Mat(1080, 1920, MatType.CV_8UC1, Scalar.All(40));
        var shopBounds = new NormalizedRect(0.10, 0.15, 0.10, 0.20);
        var layout = new StubLayoutRecognizer(LayoutRecognition.Succeeded(
            layoutVersion: 23,
            confidence: 0.96,
            slots: [new CardSlot(CardZone.Shop, 0, shopBounds)],
            goldBounds: new NormalizedRect(0.01, 0.01, 0.05, 0.05),
            tavernTierBounds: new NormalizedRect(0.06, 0.01, 0.05, 0.05),
            handCapacity: 10,
            boardCapacity: 7));
        var matcher = new StubCardMatcher(new CardMatch("CARD_A", false, 0.97));
        var digits = new StubDigitRecognizer(7, 4, 0.98);
        var scenes = new StubSceneRecognizer(new SceneRecognition(GamePhase.Shopping, 0.99));
        var recognizer = new SnapshotRecognizer(layout, matcher, digits, scenes);

        var result = recognizer.Recognize(frame, DateTimeOffset.UnixEpoch);

        Assert.True(result.Snapshot.IsActionable);
        Assert.Equal(23, result.Snapshot.LayoutVersion);
        Assert.Equal(7, result.Snapshot.Gold);
        Assert.Equal(4, result.Snapshot.TavernTier);
        var card = Assert.Single(result.Snapshot.Shop);
        Assert.Equal("CARD_A", card.CardId);
        Assert.Equal(0, card.SlotIndex);
        Assert.Equal(shopBounds, card.Bounds);
        Assert.Equal(10, result.Snapshot.FreeHandSlots);
        Assert.Empty(result.Snapshot.Board);
        Assert.Equal(0.98, result.Gold.Confidence);
        Assert.Equal(shopBounds, result.Cards.Single().Bounds);
    }

    [Fact]
    public void Recognize_MarksSnapshotNotActionable_WhenCriticalAnchorsFail()
    {
        using var frame = new Mat(1080, 1920, MatType.CV_8UC1, Scalar.All(40));
        var recognizer = new SnapshotRecognizer(
            new StubLayoutRecognizer(LayoutRecognition.Failed()),
            new StubCardMatcher(new CardMatch("CARD_A", false, 0.97)),
            new StubDigitRecognizer(7, 4, 0.98),
            new StubSceneRecognizer(new SceneRecognition(GamePhase.Shopping, 0.99)));

        var result = recognizer.Recognize(frame, DateTimeOffset.UnixEpoch);

        Assert.False(result.Snapshot.IsActionable);
        Assert.Equal(GamePhase.Unknown, result.Snapshot.GamePhase);
        Assert.Equal(0, result.Snapshot.LayoutVersion);
    }

    private sealed class StubLayoutRecognizer(LayoutRecognition result) : ILayoutRecognizer
    {
        public LayoutRecognition Recognize(Mat frame) => result;
    }

    private sealed class StubCardMatcher(CardMatch result) : ICardMatcher
    {
        public CardMatch Match(Mat cardImage) => result;
    }

    private sealed class StubDigitRecognizer(int gold, int tier, double confidence) : IDigitRecognizer
    {
        public DigitRecognition Recognize(Mat image, NormalizedRect bounds, string label) =>
            new(label == "gold" ? gold : tier, bounds, confidence);
    }

    private sealed class StubSceneRecognizer(SceneRecognition result) : ISceneRecognizer
    {
        public SceneRecognition Recognize(Mat frame) => result;
    }
}
