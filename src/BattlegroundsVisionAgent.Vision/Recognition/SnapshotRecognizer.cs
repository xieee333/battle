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
    NormalizedRect GoldCoinBounds = default,
    NormalizedRect GoldResourceBounds = default)
{
    public static LayoutRecognition Succeeded(long layoutVersion, double confidence, IReadOnlyList<CardSlot> slots,
        NormalizedRect goldBounds, NormalizedRect tavernTierBounds, int handCapacity, int boardCapacity,
        bool hasPendingTripleReward = false, bool hasUnknownBlockingUi = false,
        NormalizedRect armorBounds = default, NormalizedRect goldCoinBounds = default,
        NormalizedRect goldResourceBounds = default) =>
        new(true, layoutVersion, confidence, slots, goldBounds, tavernTierBounds, handCapacity, boardCapacity,
            hasPendingTripleReward, hasUnknownBlockingUi, armorBounds, goldCoinBounds, goldResourceBounds);

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
    ISceneRecognizer sceneRecognizer,
    ICardOccupancyDetector? occupancyDetector = null,
    ITavernTierRecognizer? tavernTierRecognizer = null) : IDisposable
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

        var gold = layout.GoldResourceBounds.Width > 0 && layout.GoldResourceBounds.Height > 0
            ? digitRecognizer.Recognize(frame, layout.GoldResourceBounds, "gold-resource")
            : digitRecognizer.Recognize(frame, layout.GoldBounds, "gold");
        if (!gold.IsKnown && layout.GoldResourceBounds.Width > 0 && layout.GoldResourceBounds.Height > 0)
            gold = digitRecognizer.Recognize(frame, layout.GoldBounds, "gold");
        if (!gold.IsKnown && layout.GoldResourceBounds.Width <= 0
            && layout.GoldCoinBounds.Width > 0 && layout.GoldCoinBounds.Height > 0)
        {
            var coinRecognition = GoldCoinRecognizer.Recognize(frame, layout.GoldCoinBounds);
            if (coinRecognition.IsKnown)
                gold = new DigitRecognition(coinRecognition.Value, layout.GoldBounds, coinRecognition.Confidence);
        }
        var tier = tavernTierRecognizer?.Recognize(frame, layout.TavernTierBounds)
            ?? digitRecognizer.Recognize(frame, layout.TavernTierBounds, "tavern-tier");
        var armor = layout.ArmorBounds.Width > 0 && layout.ArmorBounds.Height > 0
            ? digitRecognizer.Recognize(frame, layout.ArmorBounds, "armor")
            : null;
        var cards = new List<RecognizedCard>();
        var visibleSlots = layout.Slots.Where(slot =>
            slot.Zone != CardZone.Discover || scene.GamePhase == GamePhase.Discover);
        foreach (var slot in visibleSlots)
        {
            using var cardImage = Crop(frame, slot.Bounds);
            var match = cardMatcher is IZoneAwareCardMatcher zoneAwareMatcher
                ? zoneAwareMatcher.Match(cardImage, slot.Zone)
                : cardMatcher.Match(cardImage);
            var occupancy = occupancyDetector?.Detect(cardImage, slot.Zone)
                ?? new CardOccupancyDetection(true, 1);
            var observation = new CardObservation(match.CardId ?? UnknownCardId, slot.Zone, slot.SlotIndex,
                match.IsGolden, slot.Bounds, match.Confidence, match.Kind, occupancy.IsOccupied);
            cards.Add(new RecognizedCard(observation, slot.Bounds, slot.SlotIndex, match.Confidence));
        }

        var confidence = new[] { layout.Confidence, gold.Confidence, tier.Confidence, scene.Confidence,
                armor?.Confidence ?? 1 }
            .Concat(cards.Where(card => card.Observation.IsOccupied
                    && !string.Equals(card.Observation.CardId, UnknownCardId, StringComparison.OrdinalIgnoreCase))
                .Select(card => card.Confidence))
            .DefaultIfEmpty(0).Min();
        var unknownCard = cards.Any(card => card.Observation.CardId == UnknownCardId
            && card.Observation.IsOccupied
            && card.Observation.Kind != CardKind.Spell);
        var unknownBlockingUi = layout.HasUnknownBlockingUi || !gold.IsKnown || !tier.IsKnown
            || scene.GamePhase == GamePhase.Unknown;
        var snapshot = new GameSnapshot(layout.LayoutVersion, confidence, capturedAt, scene.GamePhase,
            gold.Value, tier.Value,
            cards.Where(card => card.Observation.CardZone == CardZone.Shop).Select(card => card.Observation).ToArray(),
            cards.Where(card => card.Observation.CardZone == CardZone.Hand && card.Observation.IsOccupied).Select(card => card.Observation).ToArray(),
            cards.Where(card => card.Observation.CardZone == CardZone.Board && card.Observation.IsOccupied).Select(card => card.Observation).ToArray(),
            cards.Where(card => card.Observation.CardZone == CardZone.Discover).Select(card => card.Observation).ToArray(),
            layout.HandCapacity, layout.BoardCapacity, layout.HasPendingTripleReward, unknownBlockingUi,
            armor?.Value, unknownCard);
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
