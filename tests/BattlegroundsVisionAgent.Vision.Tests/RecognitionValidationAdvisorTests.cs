using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Recognition;
using BattlegroundsVisionAgent.Vision.Validation;

namespace BattlegroundsVisionAgent.Vision.Tests;

public sealed class RecognitionValidationAdvisorTests
{
    [Fact]
    public void BuildRecommendations_ExplainsUnknownCardsAndUnsafeSnapshot()
    {
        var result = Result(
            gold: new DigitRecognition(null, Bounds(), 0.4),
            tier: new DigitRecognition(3, Bounds(), 0.92),
            scene: new SceneRecognition(GamePhase.Unknown, 0.3),
            cards: [Card("UNKNOWN", CardZone.Shop, 0, 0.55)]);

        var recommendations = RecognitionValidationAdvisor.BuildRecommendations(result);

        Assert.Contains(recommendations, item => item.Contains("金币未识别", StringComparison.Ordinal));
        Assert.Contains(recommendations, item => item.Contains("阶段判断不稳定", StringComparison.Ordinal));
        Assert.Contains(recommendations, item => item.Contains("卡槽未识别", StringComparison.Ordinal));
        Assert.Contains(recommendations, item => item.Contains("不会发送", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildRecommendations_UsesHumanFeedbackToSuggestTargetedFixes()
    {
        var result = Result(
            gold: new DigitRecognition(6, Bounds(), 0.95),
            tier: new DigitRecognition(4, Bounds(), 0.95),
            scene: new SceneRecognition(GamePhase.Shopping, 0.95),
            cards: [Card("CARD_A", CardZone.Shop, 1, 0.76)]);
        var feedback = new[]
        {
            new RecognitionValidationCardFeedback(
                "商店", 1, "CARD_A", "测试随从", CardKind.Minion, false, 0.76, Bounds(),
                "错误", "错误", "正确随从", "位置偏右")
        };

        var recommendations = RecognitionValidationAdvisor.BuildRecommendations(result, feedback);

        Assert.Contains(recommendations, item => item.Contains("位置不对", StringComparison.Ordinal));
        Assert.Contains(recommendations, item => item.Contains("补卡库", StringComparison.Ordinal));
    }

    [Fact]
    public void Serialize_WritesReadableFeedbackJson()
    {
        var feedback = new RecognitionValidationFeedback(
            1, DateTimeOffset.UnixEpoch, "sample.png", GamePhase.Shopping, 0.9,
            5, 0.95, 3, 0.92, null, false, [], ["补充样本"], "测试");

        var json = RecognitionValidationAdvisor.Serialize(feedback);

        Assert.Contains("sample.png", json, StringComparison.Ordinal);
        Assert.Contains("补充样本", json, StringComparison.Ordinal);
        Assert.Contains("Shopping", json, StringComparison.Ordinal);
    }

    private static SnapshotRecognitionResult Result(
        DigitRecognition gold,
        DigitRecognition tier,
        SceneRecognition scene,
        IReadOnlyList<RecognizedCard> cards)
    {
        var snapshot = new GameSnapshot(
            1, 0.55, DateTimeOffset.UnixEpoch, scene.GamePhase, gold.Value, tier.Value,
            cards.Where(card => card.Observation.CardZone == CardZone.Shop).Select(card => card.Observation).ToArray(),
            [], [], [], 10, 7, false, true);
        return new SnapshotRecognitionResult(snapshot, gold, tier, scene, cards);
    }

    private static RecognizedCard Card(string id, CardZone zone, int slot, double confidence)
    {
        var bounds = Bounds();
        return new RecognizedCard(
            new CardObservation(id, zone, slot, false, bounds, confidence, CardKind.Unknown),
            bounds, slot, confidence);
    }

    private static NormalizedRect Bounds() => new(0.1, 0.1, 0.1, 0.1);
}
