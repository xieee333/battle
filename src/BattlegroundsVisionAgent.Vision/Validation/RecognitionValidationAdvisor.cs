using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Recognition;

namespace BattlegroundsVisionAgent.Vision.Validation;

public sealed record RecognitionValidationCardFeedback(
    string Zone,
    int SlotIndex,
    string PredictedCardId,
    string PredictedName,
    CardKind PredictedKind,
    bool IsGolden,
    double Confidence,
    NormalizedRect Bounds,
    string RecognitionStatus,
    string PositionStatus,
    string ExpectedText,
    string Note);

public sealed record RecognitionValidationFeedback(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    string ImagePath,
    GamePhase Scene,
    double SceneConfidence,
    int? Gold,
    double GoldConfidence,
    int? TavernTier,
    double TavernTierConfidence,
    int? Armor,
    bool IsActionable,
    IReadOnlyList<RecognitionValidationCardFeedback> Cards,
    IReadOnlyList<string> Recommendations,
    string OverallNote,
    string CatalogVersion = "未标注版本");

public static class RecognitionValidationAdvisor
{
    public static IReadOnlyList<string> BuildRecommendations(
        SnapshotRecognitionResult result,
        IReadOnlyList<RecognitionValidationCardFeedback>? feedback = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var recommendations = new List<string>();

        if (result.Scene.GamePhase == GamePhase.Unknown || result.Scene.Confidence < 0.80)
            recommendations.Add("阶段判断不稳定：补充购物、战斗、发现各 3 张清晰截图，并避开动画、悬停提示和重连弹窗。");

        if (!result.Gold.IsKnown)
            recommendations.Add("金币未识别：确认金币区域框选覆盖完整金币条；优先补充该回合真实金币亮槽截图，不要用插件回合数字代替。");
        else if (result.Gold.Confidence < 0.85)
            recommendations.Add($"金币置信度只有 {result.Gold.Confidence:P0}：保留本图作为校准样本，并补充相同分辨率下的亮槽/暗槽样本。");

        if (!result.TavernTier.IsKnown)
            recommendations.Add("酒馆等级未识别：确认框选的是自己英雄头像左下方的星级徽章，而不是右侧升级费用；必要时补充清晰的星级徽章截图。");
        else if (result.TavernTier.Confidence < 0.85)
            recommendations.Add($"酒馆等级置信度只有 {result.TavernTier.Confidence:P0}：补充星级徽章的清晰截图，检查是否被动画或鼠标提示遮挡。");

        var visibleCards = result.Cards
            .Where(card => card.Observation.IsOccupied
                || card.Observation.CardZone is CardZone.Shop or CardZone.Discover)
            .ToArray();
        var unknownCards = visibleCards.Where(card => card.Observation.CardId == "UNKNOWN").ToArray();
        var lowConfidenceCards = visibleCards
            .Where(card => card.Observation.CardId != "UNKNOWN" && card.Confidence < 0.80)
            .ToArray();
        if (unknownCards.Length > 0)
        {
            var zones = string.Join("、", unknownCards
                .GroupBy(card => ZoneLabel(card.Observation.CardZone))
                .Select(group => $"{group.Key}{group.Count()} 个"));
            recommendations.Add($"有 {unknownCards.Length} 个卡槽未识别（{zones}）：先检查框选位置，再保存这些卡槽裁剪图；如果确实是新牌，补充卡库缩略图。");
        }

        if (lowConfidenceCards.Length > 0)
            recommendations.Add($"有 {lowConfidenceCards.Length} 个卡槽低于 80% 置信度：先人工确认，不要直接用于自动购买；确认正确后可作为正样本，确认错误后应提高阈值或补充负样本。");

        if (visibleCards.Length == 0)
            recommendations.Add("没有检测到卡槽：优先重新生成商店、手牌和战场布局配置，确认截图比例与 profile.json 一致。");

        if (!result.Snapshot.IsActionable)
            recommendations.Add("本次结果未达到可行动条件，当前只用于校验和采样，不会发送购买、刷新或升本输入。");

        if (feedback is not null)
        {
            var wrong = feedback.Where(item => item.RecognitionStatus == "错误").ToArray();
            var wrongPosition = feedback.Where(item => item.PositionStatus == "错误").ToArray();
            var confirmed = feedback.Where(item => item.RecognitionStatus == "正确").ToArray();

            if (wrongPosition.Length > 0)
                recommendations.Add($"你标记了 {wrongPosition.Length} 个位置不对：优先修正对应区域/卡槽坐标，再重新识别，暂时不要只调匹配阈值。");
            if (wrong.Length > 0)
            {
                var expected = wrong.Count(item => !string.IsNullOrWhiteSpace(item.ExpectedText));
                recommendations.Add(expected > 0
                    ? $"你标记了 {wrong.Length} 个识别错误，其中 {expected} 个填写了期望结果：这些反馈可用于补卡库、修正卡牌类型或建立负样本。"
                    : $"你标记了 {wrong.Length} 个识别错误：下次请填写期望名称或卡牌 ID，程序才能判断是卡库缺失、位置偏移还是阈值问题。");
            }
            if (confirmed.Length > 0 && confirmed.Any(item => item.Confidence < 0.80))
                recommendations.Add("有低置信度结果被确认正确：这些图片很适合加入正样本，但应先按区域和分辨率归档，避免直接全局降低阈值。");
        }

        return recommendations.Count == 0
            ? ["当前截图和反馈没有发现明显问题；可以继续采集不同回合、刷新后和金色牌样本。"]
            : recommendations;
    }

    public static string Serialize(RecognitionValidationFeedback feedback) => JsonSerializer.Serialize(
        feedback,
        new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() }
        });

    private static string ZoneLabel(CardZone zone) => zone switch
    {
        CardZone.Shop => "商店",
        CardZone.Hand => "手牌",
        CardZone.Board => "战场",
        CardZone.Discover => "发现",
        _ => zone.ToString()
    };
}
