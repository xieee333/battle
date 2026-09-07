using System.Globalization;
using BattlegroundsVisionAgent.Core.Domain;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Recognition;

public enum PurchaseEventKind
{
    PossiblePurchase,
    ConfirmedPurchase,
    PurchaseAndPlay
}

/// <summary>
/// Evidence assembled from more than one captured frame. It is intentionally
/// not a game action: a visual event must never be treated as permission to
/// send another input by itself.
/// </summary>
public sealed record PurchaseEvidence(
    PurchaseEventKind Kind,
    DateTimeOffset ObservedAt,
    TimeSpan ObservationGap,
    int? GoldBefore,
    int? GoldAfter,
    int? GoldSpent,
    IReadOnlyList<int> ShopChangedSlots,
    IReadOnlyList<int> HandChangedSlots,
    IReadOnlyList<int> BoardChangedSlots,
    double Confidence,
    string Reason)
{
    public int PurchaseCount => GoldSpent is >= 3
        ? Math.Max(1, (GoldSpent.Value + 1) / 3)
        : 1;

    public string ToLogLine()
    {
        static string Slots(IReadOnlyList<int> values) => values.Count == 0
            ? "-"
            : string.Join(',', values);

        var gold = GoldBefore.HasValue && GoldAfter.HasValue
            ? $"{GoldBefore.Value}->{GoldAfter.Value}"
            : "unknown";
        return string.Create(CultureInfo.InvariantCulture,
            $"事件={Kind} confidence={Confidence:P0} gap={ObservationGap.TotalMilliseconds:F0}ms "
            + $"gold={gold} spent={GoldSpent?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} "
            + $"shopSlots={Slots(ShopChangedSlots)} handSlots={Slots(HandChangedSlots)} "
            + $"boardSlots={Slots(BoardChangedSlots)} reason={Reason}");
    }
}

/// <summary>
/// Correlates adjacent and near-adjacent frames so a short action does not
/// disappear simply because recognition finished after the animation.
///
/// A refresh normally costs one gold and changes the shop. A purchase normally
/// costs three gold and changes the shop plus the hand or board. The detector
/// requires corroborating signals and reports uncertainty instead of guessing.
/// </summary>
public sealed class PurchaseEventDetector : IDisposable
{
    public static readonly TimeSpan DefaultHistoryWindow = TimeSpan.FromSeconds(4);
    public const double DefaultSlotChangeThreshold = 0.16;

    private readonly TimeSpan _historyWindow;
    private readonly double _slotChangeThreshold;
    private readonly List<Sample> _history = [];
    private string? _lastEventSignature;
    private DateTimeOffset _lastEventAt;
    private bool _disposed;

    public PurchaseEventDetector(
        TimeSpan? historyWindow = null,
        double slotChangeThreshold = DefaultSlotChangeThreshold)
    {
        _historyWindow = historyWindow ?? DefaultHistoryWindow;
        if (_historyWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(historyWindow));
        if (!double.IsFinite(slotChangeThreshold) || slotChangeThreshold is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(slotChangeThreshold));
        _slotChangeThreshold = slotChangeThreshold;
    }

    public PurchaseEvidence? Observe(Mat frame, SnapshotRecognitionResult result)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(result);
        if (frame.Empty())
            return null;

        var current = new Sample(result.Snapshot.CapturedAt, frame.Clone(), result);
        var retained = false;
        PurchaseEvidence? best = null;
        try
        {
            foreach (var previous in _history.AsEnumerable().Reverse())
            {
                var gap = current.CapturedAt - previous.CapturedAt;
                if (gap < TimeSpan.Zero || gap > _historyWindow)
                    continue;

                var candidate = Evaluate(previous, current);
                if (candidate is null || best is not null && candidate.Confidence <= best.Confidence)
                    continue;
                best = candidate;
            }

            AddToHistory(current);
            retained = true;
            if (best is null)
                return null;

            var signature = CreateSignature(best);
            if (string.Equals(signature, _lastEventSignature, StringComparison.Ordinal)
                && current.CapturedAt - _lastEventAt < TimeSpan.FromSeconds(1.5))
                return null;

            _lastEventSignature = signature;
            _lastEventAt = current.CapturedAt;
            return best;
        }
        finally
        {
            if (!retained)
                current.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var sample in _history)
            sample.Dispose();
        _history.Clear();
    }

    private PurchaseEvidence? Evaluate(Sample before, Sample after)
    {
        var beforeSnapshot = before.Result.Snapshot;
        var afterSnapshot = after.Result.Snapshot;
        if (beforeSnapshot.GamePhase != GamePhase.Shopping
            || afterSnapshot.GamePhase != GamePhase.Shopping)
            return null;

        var shop = CompareZone(before, after, CardZone.Shop);
        if (shop.ChangedSlots.Count == 0)
            return null;

        var hand = CompareZone(before, after, CardZone.Hand);
        var board = CompareZone(before, after, CardZone.Board);
        var destinationChanges = hand.ChangedSlots.Count + board.ChangedSlots.Count;
        int? goldSpent = beforeSnapshot.Gold.HasValue && afterSnapshot.Gold.HasValue
            ? beforeSnapshot.Gold.Value - afterSnapshot.Gold.Value
            : null;

        // A refresh changes the shop but costs one gold. Do not call that a buy
        // unless a destination changed as well and the visual evidence is strong.
        var hasPurchaseCost = goldSpent is >= 3;
        var hasDestinationEvidence = destinationChanges > 0;
        if (!hasPurchaseCost && !hasDestinationEvidence)
            return null;

        var kind = hasPurchaseCost && board.ChangedSlots.Count > 0 && hand.ChangedSlots.Count == 0
            ? PurchaseEventKind.PurchaseAndPlay
            : hasPurchaseCost && (hasDestinationEvidence || goldSpent!.Value % 3 == 0)
                ? PurchaseEventKind.ConfirmedPurchase
                : PurchaseEventKind.PossiblePurchase;

        var confidence = 0.35;
        if (hasPurchaseCost)
            confidence += goldSpent!.Value % 3 == 0 ? 0.30 : 0.18;
        if (hasDestinationEvidence)
            confidence += 0.25;
        if (shop.MaxDifference >= 0.35)
            confidence += 0.10;
        confidence = Math.Clamp(confidence, 0, 0.99);

        var reasonParts = new List<string>();
        if (goldSpent.HasValue)
            reasonParts.Add($"goldDelta={goldSpent.Value.ToString(CultureInfo.InvariantCulture)}");
        if (hasDestinationEvidence)
            reasonParts.Add("destination-changed");
        reasonParts.Add($"shop-diff={shop.MaxDifference:F2}");

        return new PurchaseEvidence(
            kind,
            after.CapturedAt,
            after.CapturedAt - before.CapturedAt,
            beforeSnapshot.Gold,
            afterSnapshot.Gold,
            goldSpent,
            shop.ChangedSlots,
            hand.ChangedSlots,
            board.ChangedSlots,
            confidence,
            string.Join(',', reasonParts));
    }

    private ZoneChangeSummary CompareZone(Sample before, Sample after, CardZone zone)
    {
        var beforeCards = before.Result.Cards
            .Where(card => card.Observation.CardZone == zone)
            .ToDictionary(card => card.SlotIndex);
        var afterCards = after.Result.Cards
            .Where(card => card.Observation.CardZone == zone)
            .ToDictionary(card => card.SlotIndex);
        var changedSlots = new List<int>();
        var maxDifference = 0d;

        foreach (var slotIndex in beforeCards.Keys.Union(afterCards.Keys).OrderBy(index => index))
        {
            var beforeCard = beforeCards.GetValueOrDefault(slotIndex);
            var afterCard = afterCards.GetValueOrDefault(slotIndex);
            var bounds = afterCard?.Bounds ?? beforeCard?.Bounds;
            if (bounds is null || bounds.Value.Width <= 0 || bounds.Value.Height <= 0)
                continue;

            var difference = FrameDifference(before.Frame, after.Frame, bounds.Value);
            if (beforeCard is not null && afterCard is not null
                && IsKnown(beforeCard.Observation) && IsKnown(afterCard.Observation)
                && (beforeCard.Observation.CardId != afterCard.Observation.CardId
                    || beforeCard.Observation.IsGolden != afterCard.Observation.IsGolden))
                difference = Math.Max(difference, 1);

            maxDifference = Math.Max(maxDifference, difference);
            if (difference >= _slotChangeThreshold)
                changedSlots.Add(slotIndex);
        }

        return new ZoneChangeSummary(changedSlots, maxDifference);
    }

    private void AddToHistory(Sample current)
    {
        _history.Add(current);
        var cutoff = current.CapturedAt - _historyWindow;
        for (var index = _history.Count - 1; index >= 0; index--)
        {
            if (_history[index].CapturedAt >= cutoff)
                continue;
            _history[index].Dispose();
            _history.RemoveAt(index);
        }
    }

    private static double FrameDifference(Mat before, Mat after, NormalizedRect bounds)
    {
        using var beforeCrop = Crop(before, bounds);
        using var afterCrop = Crop(after, bounds);
        if (beforeCrop.Empty() || afterCrop.Empty())
            return 0;

        using var beforeGray = ToGray(beforeCrop);
        using var afterGray = ToGray(afterCrop);
        using var beforeSmall = new Mat();
        using var afterSmall = new Mat();
        Cv2.Resize(beforeGray, beforeSmall, new Size(32, 32));
        Cv2.Resize(afterGray, afterSmall, new Size(32, 32));
        using var difference = new Mat();
        Cv2.Absdiff(beforeSmall, afterSmall, difference);
        return Cv2.Mean(difference).Val0 / 255d;
    }

    private static Mat Crop(Mat frame, NormalizedRect bounds)
    {
        var x = Math.Clamp((int)Math.Floor(bounds.X * frame.Width), 0, Math.Max(0, frame.Width - 1));
        var y = Math.Clamp((int)Math.Floor(bounds.Y * frame.Height), 0, Math.Max(0, frame.Height - 1));
        var width = Math.Min(Math.Max(1, (int)Math.Ceiling(bounds.Width * frame.Width)), frame.Width - x);
        var height = Math.Min(Math.Max(1, (int)Math.Ceiling(bounds.Height * frame.Height)), frame.Height - y);
        return new Mat(frame, new Rect(x, y, width, height)).Clone();
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

    private static bool IsKnown(CardObservation observation) =>
        !string.Equals(observation.CardId, "UNKNOWN", StringComparison.OrdinalIgnoreCase);

    private static string CreateSignature(PurchaseEvidence evidence) => string.Join('|',
        evidence.Kind,
        evidence.GoldBefore,
        evidence.GoldAfter,
        string.Join(',', evidence.ShopChangedSlots),
        string.Join(',', evidence.HandChangedSlots),
        string.Join(',', evidence.BoardChangedSlots));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class Sample(DateTimeOffset capturedAt, Mat frame, SnapshotRecognitionResult result) : IDisposable
    {
        public DateTimeOffset CapturedAt { get; } = capturedAt;
        public Mat Frame { get; } = frame;
        public SnapshotRecognitionResult Result { get; } = result;

        public void Dispose() => Frame.Dispose();
    }

    private sealed record ZoneChangeSummary(IReadOnlyList<int> ChangedSlots, double MaxDifference);
}
