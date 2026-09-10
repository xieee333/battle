using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Catalog;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Recognition;

public enum ScreenshotRecognitionStatus
{
    Confirmed,
    Review,
    Unknown
}

public sealed record ScreenshotCardResult(
    CardZone Zone,
    int SlotIndex,
    string CardId,
    string? NameZhCn,
    bool IsGolden,
    CardKind Kind,
    double Confidence,
    NormalizedRect Bounds,
    ScreenshotRecognitionStatus Status);

public sealed record ScreenshotRecognitionReport(
    string? SourcePath,
    int Width,
    int Height,
    FrameQuality Quality,
    string QualityReason,
    SceneRecognition Scene,
    DigitRecognition Gold,
    DigitRecognition TavernTier,
    DigitRecognition? Armor,
    IReadOnlyList<ScreenshotCardResult> Cards,
    bool IsActionable,
    IReadOnlyList<string> BlockingReasons)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public IReadOnlyList<ScreenshotCardResult> ForZone(CardZone zone) =>
        Cards.Where(card => card.Zone == zone).OrderBy(card => card.SlotIndex).ToArray();

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public string ToText()
    {
        var lines = new List<string>
        {
            $"截图：{SourcePath ?? "(memory)"} · {Width}×{Height}",
            $"质量：{Quality}（{QualityReason}）",
            $"场景：{FormatScene(Scene)}",
            $"金币：{FormatDigit(Gold)} · 酒馆等级：{FormatDigit(TavernTier)} · 护甲：{FormatDigit(Armor)}",
            $"安全状态：{(IsActionable ? "可行动" : "不会发送输入")}" 
        };

        if (BlockingReasons.Count > 0)
            lines.Add($"阻断原因：{string.Join("、", BlockingReasons)}");

        foreach (var (zone, label) in new[]
        {
            (CardZone.Shop, "商店"),
            (CardZone.Hand, "手牌"),
            (CardZone.Board, "战场"),
            (CardZone.Discover, "发现")
        })
        {
            var cards = ForZone(zone);
            lines.Add($"{label}（{cards.Count}）:");
            if (cards.Count == 0)
            {
                lines.Add("  （无）");
                continue;
            }

            foreach (var card in cards)
            {
                var name = string.IsNullOrWhiteSpace(card.NameZhCn) ? "未知卡牌" : card.NameZhCn;
                var golden = card.IsGolden ? " · 金色" : string.Empty;
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"  [{card.SlotIndex}] {name} ({card.CardId}){golden} · {card.Status} · {card.Confidence:P1}"));
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static ScreenshotRecognitionReport Empty(
        int width,
        int height,
        FrameQualityResult quality,
        string? sourcePath = null)
    {
        var reasons = new List<string>();
        if (quality.Quality != FrameQuality.Good)
            reasons.Add($"frame-quality-{quality.Quality.ToString().ToLowerInvariant()}");
        if (reasons.Count == 0)
            reasons.Add("recognition-unavailable");

        var emptyBounds = new NormalizedRect(0, 0, 0, 0);
        var unknownDigit = new DigitRecognition(null, emptyBounds, 0);
        return new ScreenshotRecognitionReport(
            sourcePath ?? quality.SourcePath,
            width,
            height,
            quality.Quality,
            quality.Reason,
            new SceneRecognition(GamePhase.Unknown, 0),
            unknownDigit,
            unknownDigit,
            null,
            [],
            false,
            reasons);
    }

    private static string FormatScene(SceneRecognition scene) =>
        scene.GamePhase == GamePhase.Unknown
            ? $"未知（{scene.Confidence:P1}）"
            : $"{scene.GamePhase}（{scene.Confidence:P1}）";

    private static string FormatDigit(DigitRecognition? digit) =>
        digit?.Value is int value
            ? string.Create(CultureInfo.InvariantCulture, $"{value}（{digit.Confidence:P1}）")
            : $"未知（{digit?.Confidence ?? 0:P1}）";
}

public static class ScreenshotRecognitionReportBuilder
{
    private const double ConfirmedCardConfidence = 0.90;

    public static ScreenshotRecognitionReport FromSnapshot(
        int width,
        int height,
        FrameQualityResult quality,
        SnapshotRecognitionResult result,
        IReadOnlyDictionary<string, CardCatalogEntry> catalog,
        string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(catalog);

        var cards = result.Cards
            .OrderBy(card => card.Observation.CardZone)
            .ThenBy(card => card.SlotIndex)
            .Select(card =>
            {
                var observation = card.Observation;
                var known = !string.IsNullOrWhiteSpace(observation.CardId)
                    && !string.Equals(observation.CardId, "UNKNOWN", StringComparison.OrdinalIgnoreCase);
                var status = !known || observation.Kind == CardKind.Unknown
                    ? ScreenshotRecognitionStatus.Unknown
                    : card.Confidence >= ConfirmedCardConfidence
                        ? ScreenshotRecognitionStatus.Confirmed
                        : ScreenshotRecognitionStatus.Review;
                catalog.TryGetValue(observation.CardId, out var entry);
                return new ScreenshotCardResult(
                    observation.CardZone,
                    observation.SlotIndex,
                    known ? observation.CardId : "UNKNOWN",
                    entry?.NameZhCn,
                    observation.IsGolden,
                    observation.Kind,
                    Math.Clamp(card.Confidence, 0, 1),
                    card.Bounds,
                    status);
            })
            .ToArray();

        var blockingReasons = new List<string>();
        if (quality.Quality != FrameQuality.Good)
            blockingReasons.Add($"frame-quality-{quality.Quality.ToString().ToLowerInvariant()}");
        if (result.Scene.GamePhase == GamePhase.Unknown)
            blockingReasons.Add("scene-unknown");
        if (!result.Gold.IsKnown)
            blockingReasons.Add("gold-unknown");
        if (!result.TavernTier.IsKnown)
            blockingReasons.Add("tavern-tier-unknown");
        if (result.Snapshot.HasUnknownBlockingUi)
            blockingReasons.Add("unknown-ui");
        if (cards.Any(card => card.Status == ScreenshotRecognitionStatus.Unknown))
            blockingReasons.Add("unknown-card");
        if (cards.Any(card => card.Status == ScreenshotRecognitionStatus.Review))
            blockingReasons.Add("card-review");
        if (result.Scene.GamePhase is not (GamePhase.Shopping or GamePhase.Discover))
            blockingReasons.Add("scene-not-actionable");

        var actionable = result.Snapshot.IsActionable
            && quality.Quality == FrameQuality.Good
            && cards.All(card => card.Status == ScreenshotRecognitionStatus.Confirmed);
        return new ScreenshotRecognitionReport(
            sourcePath ?? quality.SourcePath,
            width,
            height,
            quality.Quality,
            quality.Reason,
            result.Scene,
            result.Gold,
            result.TavernTier,
            result.Armor,
            cards,
            actionable,
            blockingReasons.Distinct(StringComparer.Ordinal).ToArray());
    }
}

public sealed class ScreenshotRecognitionService : IDisposable
{
    private readonly VisionRecognitionPipeline _pipeline;
    private readonly IReadOnlyDictionary<string, CardCatalogEntry> _catalog;
    private bool _disposed;

    private ScreenshotRecognitionService(
        VisionRecognitionPipeline pipeline,
        IReadOnlyDictionary<string, CardCatalogEntry> catalog)
    {
        _pipeline = pipeline;
        _catalog = catalog;
    }

    public static ScreenshotRecognitionService Load(string profilePath, string catalogDatabasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogDatabasePath);
        var catalog = new CardCatalog(catalogDatabasePath).GetEntries()
            .ToDictionary(entry => entry.CardId, StringComparer.OrdinalIgnoreCase);
        return new ScreenshotRecognitionService(
            VisionRecognitionPipeline.Load(profilePath, catalogDatabasePath),
            catalog);
    }

    public ScreenshotRecognitionReport Recognize(
        Mat image,
        string? sourcePath = null,
        DateTimeOffset? capturedAt = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(image);
        var quality = FrameQualityAnalyzer.Analyze(image, sourcePath);
        if (quality.Quality == FrameQuality.Garbage)
            return ScreenshotRecognitionReport.Empty(image.Width, image.Height, quality, sourcePath);

        var result = _pipeline.Recognizer.Recognize(image, capturedAt ?? DateTimeOffset.Now);
        return ScreenshotRecognitionReportBuilder.FromSnapshot(
            image.Width,
            image.Height,
            quality,
            result,
            _catalog,
            sourcePath);
    }

    public ScreenshotRecognitionReport RecognizeFile(string imagePath)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var fullPath = Path.GetFullPath(imagePath);
        using var image = Cv2.ImRead(fullPath, ImreadModes.Color);
        if (image.Empty())
        {
            var quality = new FrameQualityResult(
                fullPath,
                FrameQuality.Garbage,
                "decode-failed",
                new FrameQualityMetrics(0, 0, 0, 0, 0, 0, 0, 0));
            return ScreenshotRecognitionReport.Empty(0, 0, quality, fullPath);
        }

        return Recognize(image, fullPath, File.GetLastWriteTimeUtc(fullPath));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _pipeline.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
