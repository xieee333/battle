using BattlegroundsVisionAgent.Core.Domain;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Recognition;

public sealed record CardSlot(CardZone Zone, int SlotIndex, NormalizedRect Bounds);

public sealed record LayoutRecognition(
    bool IsSuccess,
    long LayoutVersion,
    double Confidence,
    IReadOnlyList<CardSlot> Slots,
    NormalizedRect GoldBounds,
    NormalizedRect TavernTierBounds,
    int HandCapacity,
    int BoardCapacity,
    bool HasPendingTripleReward,
    bool HasUnknownBlockingUi,
    NormalizedRect ArmorBounds = default,
    NormalizedRect GoldCoinBounds = default)
{
    public static LayoutRecognition Succeeded(long layoutVersion, double confidence, IReadOnlyList<CardSlot> slots,
        NormalizedRect goldBounds, NormalizedRect tavernTierBounds, int handCapacity, int boardCapacity,
        bool hasPendingTripleReward = false, bool hasUnknownBlockingUi = false,
        NormalizedRect armorBounds = default, NormalizedRect goldCoinBounds = default) =>
        new(true, layoutVersion, confidence, slots, goldBounds, tavernTierBounds, handCapacity, boardCapacity,
            hasPendingTripleReward, hasUnknownBlockingUi, armorBounds, goldCoinBounds);

    public static LayoutRecognition Failed() => new(false, 0, 0, [], default, default, 0, 0, false, true);
}

public interface ILayoutRecognizer
{
    LayoutRecognition Recognize(Mat frame);
}

public sealed record RecognizedCard(CardObservation Observation, NormalizedRect Bounds, int SlotIndex, double Confidence);

public sealed record SnapshotRecognitionResult(
    GameSnapshot Snapshot,
    DigitRecognition Gold,
    DigitRecognition TavernTier,
    SceneRecognition Scene,
    IReadOnlyList<RecognizedCard> Cards,
    DigitRecognition? Armor = null);

public sealed class SnapshotRecognizer(
    ILayoutRecognizer layoutRecognizer,
    ICardMatcher cardMatcher,
    IDigitRecognizer digitRecognizer,
    ISceneRecognizer sceneRecognizer) : IDisposable
{
    private const string UnknownCardId = "UNKNOWN";
    public SnapshotRecognitionResult Recognize(Mat frame, DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var scene = sceneRecognizer.Recognize(frame);
        var layout = layoutRecognizer.Recognize(frame);
        if (!layout.IsSuccess && scene.GamePhase != GamePhase.Unknown
            && layoutRecognizer is TemplateLayoutRecognizer templateLayout)
            layout = templateLayout.RecognizeUsingProfileFallback(frame);
        if (!layout.IsSuccess)
            return Failed(capturedAt);

        var gold = digitRecognizer.Recognize(frame, layout.GoldBounds, "gold");
        if (layout.GoldCoinBounds.Width > 0 && layout.GoldCoinBounds.Height > 0)
        {
            var coinRecognition = GoldCoinRecognizer.Recognize(frame, layout.GoldCoinBounds);
            if (coinRecognition.IsKnown)
                gold = new DigitRecognition(coinRecognition.Value, layout.GoldBounds, coinRecognition.Confidence);
        }
        var tier = digitRecognizer.Recognize(frame, layout.TavernTierBounds, "tavern-tier");
        var armor = layout.ArmorBounds.Width > 0 && layout.ArmorBounds.Height > 0
            ? digitRecognizer.Recognize(frame, layout.ArmorBounds, "armor")
            : null;
        var cards = new List<RecognizedCard>();
        foreach (var slot in layout.Slots)
        {
            using var cardImage = Crop(frame, slot.Bounds);
            var match = cardMatcher is IZoneAwareCardMatcher zoneAwareMatcher
                ? zoneAwareMatcher.Match(cardImage, slot.Zone)
                : cardMatcher.Match(cardImage);
            var observation = new CardObservation(match.CardId ?? UnknownCardId, slot.Zone, slot.SlotIndex,
                match.IsGolden, slot.Bounds, match.Confidence, match.Kind);
            cards.Add(new RecognizedCard(observation, slot.Bounds, slot.SlotIndex, match.Confidence));
        }

        var confidence = new[] { layout.Confidence, gold.Confidence, tier.Confidence, scene.Confidence,
                armor?.Confidence ?? 1 }
            .Concat(cards.Select(card => card.Confidence)).DefaultIfEmpty(0).Min();
        var unknownBlockingUi = layout.HasUnknownBlockingUi || !gold.IsKnown || !tier.IsKnown || scene.GamePhase == GamePhase.Unknown
            || cards.Any(card => card.Observation.CardId == UnknownCardId
                && card.Observation.Kind != CardKind.Spell);
        var snapshot = new GameSnapshot(layout.LayoutVersion, confidence, capturedAt, scene.GamePhase,
            gold.Value, tier.Value,
            cards.Where(card => card.Observation.CardZone == CardZone.Shop).Select(card => card.Observation).ToArray(),
            cards.Where(card => card.Observation.CardZone == CardZone.Hand).Select(card => card.Observation).ToArray(),
            cards.Where(card => card.Observation.CardZone == CardZone.Board).Select(card => card.Observation).ToArray(),
            cards.Where(card => card.Observation.CardZone == CardZone.Discover).Select(card => card.Observation).ToArray(),
            layout.HandCapacity, layout.BoardCapacity, layout.HasPendingTripleReward, unknownBlockingUi,
            armor?.Value);
        return new SnapshotRecognitionResult(snapshot, gold, tier, scene, cards, armor);
    }

    private static SnapshotRecognitionResult Failed(DateTimeOffset capturedAt)
    {
        var empty = new NormalizedRect(0, 0, 0, 0);
        var digit = new DigitRecognition(null, empty, 0);
        var scene = new SceneRecognition(GamePhase.Unknown, 0);
        var snapshot = new GameSnapshot(0, 0, capturedAt, scene.GamePhase, null, null, [], [], [], [], 0, 0, false, true);
        return new SnapshotRecognitionResult(snapshot, digit, digit, scene, []);
    }

    private static Mat Crop(Mat frame, NormalizedRect bounds)
    {
        var x = (int)Math.Floor(bounds.X * frame.Width);
        var y = (int)Math.Floor(bounds.Y * frame.Height);
        var width = Math.Max(1, (int)Math.Ceiling(bounds.Width * frame.Width));
        var height = Math.Max(1, (int)Math.Ceiling(bounds.Height * frame.Height));
        return new Mat(frame, new Rect(x, y, Math.Min(width, frame.Width - x), Math.Min(height, frame.Height - y))).Clone();
    }

    public void Dispose()
    {
        if (cardMatcher is IDisposable disposable)
            disposable.Dispose();
    }
}
