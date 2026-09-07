using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Recognition;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Tests;

public sealed class PurchaseEventDetectorTests
{
    [Fact]
    public void Observe_ConfirmsPurchaseWhenGoldShopAndHandChange()
    {
        using var beforeFrame = Frame(shop: 30, hand: 30, board: 30);
        using var afterFrame = Frame(shop: 220, hand: 220, board: 30);
        using var detector = new PurchaseEventDetector();

        var before = Result(DateTimeOffset.UnixEpoch, 9);
        var after = Result(DateTimeOffset.UnixEpoch.AddMilliseconds(700), 6);

        Assert.Null(detector.Observe(beforeFrame, before));
        var evidence = detector.Observe(afterFrame, after);

        Assert.NotNull(evidence);
        Assert.Equal(PurchaseEventKind.ConfirmedPurchase, evidence.Kind);
        Assert.Equal(3, evidence.GoldSpent);
        Assert.Contains(0, evidence.ShopChangedSlots);
        Assert.Contains(0, evidence.HandChangedSlots);
    }

    [Fact]
    public void Observe_RejectsRefreshAsPurchase()
    {
        using var beforeFrame = Frame(shop: 30, hand: 30, board: 30);
        using var afterFrame = Frame(shop: 220, hand: 30, board: 30);
        using var detector = new PurchaseEventDetector();

        detector.Observe(beforeFrame, Result(DateTimeOffset.UnixEpoch, 9));
        var evidence = detector.Observe(
            afterFrame,
            Result(DateTimeOffset.UnixEpoch.AddMilliseconds(500), 8));

        Assert.Null(evidence);
    }

    [Fact]
    public void ObserveRecognizesPurchaseThatWasPlayedBeforeNextFrame()
    {
        using var beforeFrame = Frame(shop: 30, hand: 30, board: 30);
        using var afterFrame = Frame(shop: 220, hand: 30, board: 220);
        using var detector = new PurchaseEventDetector();

        detector.Observe(beforeFrame, Result(DateTimeOffset.UnixEpoch, 9));
        var evidence = detector.Observe(
            afterFrame,
            Result(DateTimeOffset.UnixEpoch.AddSeconds(1), 6));

        Assert.NotNull(evidence);
        Assert.Equal(PurchaseEventKind.PurchaseAndPlay, evidence.Kind);
        Assert.Contains(0, evidence.BoardChangedSlots);
        Assert.Empty(evidence.HandChangedSlots);
    }

    private static SnapshotRecognitionResult Result(DateTimeOffset capturedAt, int gold)
    {
        var shopBounds = new NormalizedRect(0.05, 0.05, 0.25, 0.35);
        var handBounds = new NormalizedRect(0.05, 0.55, 0.25, 0.35);
        var boardBounds = new NormalizedRect(0.55, 0.05, 0.25, 0.35);
        var cards = new[]
        {
            Card(CardZone.Shop, shopBounds),
            Card(CardZone.Hand, handBounds),
            Card(CardZone.Board, boardBounds)
        };
        var snapshot = new GameSnapshot(
            layoutVersion: 1,
            confidence: 1,
            capturedAt,
            GamePhase.Shopping,
            gold,
            tavernTier: 1,
            [cards[0].Observation],
            [cards[1].Observation],
            [cards[2].Observation],
            [],
            handCapacity: 10,
            boardCapacity: 7,
            hasPendingTripleReward: false,
            hasUnknownBlockingUi: false);
        var digitBounds = new NormalizedRect(0.8, 0.8, 0.1, 0.1);
        return new SnapshotRecognitionResult(
            snapshot,
            new DigitRecognition(gold, digitBounds, 1),
            new DigitRecognition(1, digitBounds, 1),
            new SceneRecognition(GamePhase.Shopping, 1),
            cards);
    }

    private static RecognizedCard Card(CardZone zone, NormalizedRect bounds) =>
        new(new CardObservation("UNKNOWN", zone, 0, false, bounds, 0.5), bounds, 0, 0.5);

    private static Mat Frame(byte shop, byte hand, byte board)
    {
        var frame = new Mat(100, 100, MatType.CV_8UC1, Scalar.All(30));
        Cv2.Rectangle(frame, new Rect(5, 5, 25, 35), Scalar.All(shop), -1);
        Cv2.Rectangle(frame, new Rect(5, 55, 25, 35), Scalar.All(hand), -1);
        Cv2.Rectangle(frame, new Rect(55, 5, 25, 35), Scalar.All(board), -1);
        return frame;
    }
}
