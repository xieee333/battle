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

/// <summary>
/// Human-readable evidence for an offline or live recognition result. The
/// evidence describes which configured region/feature family was used; it is
/// informational and never relaxes the safety gates.
/// </summary>
public sealed record RecognitionEvidence(
    string Area,
    string Basis,
    string Result,
    double? Confidence = null);

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
    public IReadOnlyList<RecognitionEvidence> Evidence { get; init; } = [];

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

        lines.Add("判断依据：");
        if (Evidence.Count == 0)
            lines.Add("  （无）");
        else
        {
            foreach (var evidence in Evidence)
            {
                var confidence = evidence.Confidence is double value
                    ? string.Create(CultureInfo.InvariantCulture, $" · {value:P1}")
                    : string.Empty;
                lines.Add($"  [{evidence.Area}] {evidence.Basis} => {evidence.Result}{confidence}");
            }
        }

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
            reasons)
        {
            Evidence =
            [
                new RecognitionEvidence(
                    "画面质量",
                    "解码、尺寸、亮度、暗色比例、纹理和感知哈希质量门",
                    quality.Reason)
            ]
        };
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
            blockingReasons.Distinct(StringComparer.Ordinal).ToArray())
        {
            Evidence = BuildEvidence(quality, result, cards)
        };
    }

    private static IReadOnlyList<RecognitionEvidence> BuildEvidence(
        FrameQualityResult quality,
        SnapshotRecognitionResult result,
        IReadOnlyList<ScreenshotCardResult> cards)
    {
        var metrics = quality.Metrics;
        var evidence = new List<RecognitionEvidence>
        {
            new(
                "画面质量",
                "解码、尺寸、亮度、暗色比例、纹理和感知哈希质量门",
                string.Create(CultureInfo.InvariantCulture,
                    $"{quality.Reason}; {metrics.Width}×{metrics.Height}，亮度均值 {metrics.BrightnessMean:F1}，边缘比例 {metrics.EdgeRatio:P1}")),
            new(
                "场景",
                "整帧感知哈希与当前 profile 的购物/战斗/发现模板比较",
                result.Scene.GamePhase.ToString(),
                result.Scene.Confidence),
            new(
                "金币",
                "profile 金币资源 ROI，优先当前/上限数字，必要时回退数字模板",
                FormatDigitResult(result.Gold),
                result.Gold.Confidence),
            new(
                "酒馆等级",
                "英雄头像本数 ROI 的金色星点连通组件（无可靠星点时保持未知）",
                FormatDigitResult(result.TavernTier),
                result.TavernTier.Confidence)
        };

        if (result.Armor is not null)
        {
            evidence.Add(new RecognitionEvidence(
                "护甲",
                "profile 护甲 ROI + armor 数字模板",
                FormatDigitResult(result.Armor),
                result.Armor.Confidence));
        }

        foreach (var card in cards)
        {
            var basis = card.Zone switch
            {
                CardZone.Hand => "手牌区当前只做占用/位置安全判断，未套用商店卡图匹配",
                CardZone.Discover => "发现区当前只做候选位置安全判断，未套用商店卡图匹配",
                _ => "卡面插画缩略图 ORB 特征、透视内点比例、描述子相似度和候选差值"
            };
            var resultText = card.Status switch
            {
                ScreenshotRecognitionStatus.Confirmed => card.NameZhCn ?? card.CardId,
                ScreenshotRecognitionStatus.Review => "候选存在但置信度/差值不足",
                _ when card.Kind == CardKind.Spell => "检测为法术版式；当前随从卡库不参与冒充匹配",
                _ => "未达到占用或卡牌匹配安全阈值"
            };
            evidence.Add(new RecognitionEvidence(
                $"{ZoneLabel(card.Zone)}槽位",
                basis,
                $"[{card.SlotIndex}] {resultText}",
                card.Confidence));
        }

        return evidence;
    }

    private static string FormatDigitResult(DigitRecognition? digit) =>
        digit?.Value is int value
            ? string.Create(CultureInfo.InvariantCulture, $"{value}（已确认）")
            : "未知（未通过模板/区域安全门）";

    private static string ZoneLabel(CardZone zone) => zone switch
    {
        CardZone.Shop => "商店",
        CardZone.Hand => "手牌",
        CardZone.Board => "战场",
        CardZone.Discover => "发现",
        _ => "卡牌"
    };
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
