using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Catalog;
using BattlegroundsVisionAgent.Vision.Recognition;

namespace BattlegroundsVisionAgent.Vision.Tests;

public sealed class ScreenshotRecognitionReportTests
{
    [Fact]
    public void FromSnapshot_ListsNamesAndGroupsCardsByZone()
    {
        var shopBounds = new NormalizedRect(0.1, 0.2, 0.1, 0.2);
        var handBounds = new NormalizedRect(0.2, 0.7, 0.1, 0.2);
        var shopObservation = new CardObservation("CARD_A", CardZone.Shop, 0, false, shopBounds, 0.94, CardKind.Minion);
        var handObservation = new CardObservation("CARD_B", CardZone.Hand, 1, true, handBounds, 0.82, CardKind.Minion);
        var snapshot = new GameSnapshot(
            3,
            0.82,
            DateTimeOffset.UnixEpoch,
            GamePhase.Shopping,
            6,
            3,
            [shopObservation],
            [handObservation],
            [],
            [],
            10,
            7,
            false,
            false,
            armor: 12);
        var result = new SnapshotRecognitionResult(
            snapshot,
            new DigitRecognition(6, new NormalizedRect(0.01, 0.01, 0.02, 0.02), 0.99),
            new DigitRecognition(3, new NormalizedRect(0.03, 0.01, 0.02, 0.02), 0.98),
            new SceneRecognition(GamePhase.Shopping, 0.96),
            [new RecognizedCard(shopObservation, shopBounds, 0, 0.94), new RecognizedCard(handObservation, handBounds, 1, 0.82)],
            new DigitRecognition(12, new NormalizedRect(0.05, 0.01, 0.02, 0.02), 0.95));
        var quality = new FrameQualityResult(
            "test.png",
            FrameQuality.Good,
            "ok",
            new FrameQualityMetrics(1920, 1080, 100, 22, 0.1, 0.1, 0.05, 42));

        var report = ScreenshotRecognitionReportBuilder.FromSnapshot(
            1920,
            1080,
            quality,
            result,
            new Dictionary<string, CardCatalogEntry>
            {
                ["CARD_A"] = new("CARD_A", "测试甲", 2, "a.png"),
                ["CARD_B"] = new("CARD_B", "测试乙", 3, "b.png")
            });

        Assert.Equal("测试甲", Assert.Single(report.ForZone(CardZone.Shop)).NameZhCn);
        var hand = Assert.Single(report.ForZone(CardZone.Hand));
        Assert.Equal("测试乙", hand.NameZhCn);
        Assert.True(hand.IsGolden);
        Assert.Equal(ScreenshotRecognitionStatus.Review, hand.Status);
        Assert.Equal(6, report.Gold.Value);
        Assert.Equal(3, report.TavernTier.Value);
        Assert.Equal(12, report.Armor?.Value);
        Assert.Contains(report.Evidence, evidence => evidence.Area == "场景");
        Assert.Contains(report.Evidence, evidence => evidence.Area == "商店槽位");
    }

    [Fact]
    public void FromSnapshot_MarksUnknownCardsAndBlockingReasons()
    {
        var unknown = new CardObservation(
            "UNKNOWN",
            CardZone.Board,
            0,
            false,
            new NormalizedRect(0.4, 0.4, 0.1, 0.2),
            0.31,
            CardKind.Unknown);
        var snapshot = new GameSnapshot(
            0,
            0.31,
            DateTimeOffset.UnixEpoch,
            GamePhase.Unknown,
            null,
            null,
            [],
            [],
            [unknown],
            [],
            7,
            7,
            false,
            true);
        var result = new SnapshotRecognitionResult(
            snapshot,
            new DigitRecognition(null, default, 0.2),
            new DigitRecognition(null, default, 0.2),
            new SceneRecognition(GamePhase.Unknown, 0.2),
            [new RecognizedCard(unknown, unknown.Bounds, 0, 0.31)]);
        var quality = new FrameQualityResult(
            "bad.png",
            FrameQuality.Review,
            "low-texture",
            new FrameQualityMetrics(1920, 1080, 22, 3, 0.01, 0.7, 0.001, 7));

        var report = ScreenshotRecognitionReportBuilder.FromSnapshot(1920, 1080, quality, result, new Dictionary<string, CardCatalogEntry>());

        var card = Assert.Single(report.ForZone(CardZone.Board));
        Assert.Equal(ScreenshotRecognitionStatus.Unknown, card.Status);
        Assert.False(report.IsActionable);
        Assert.Contains("frame-quality-review", report.BlockingReasons);
        Assert.Contains("scene-unknown", report.BlockingReasons);
        Assert.Contains("gold-unknown", report.BlockingReasons);
        Assert.Contains("unknown-card", report.BlockingReasons);
    }

    [Fact]
    public void ToJsonAndText_ExposeAllZonesForCliAndUi()
    {
        var report = ScreenshotRecognitionReport.Empty(
            640,
            360,
            new FrameQualityResult(
                "sample.png",
                FrameQuality.Garbage,
                "mostly-white",
                new FrameQualityMetrics(640, 360, 252, 2, 0.99, 0, 0, 99)));

        var json = report.ToJson();
        var text = report.ToText();

        Assert.Contains("mostly-white", json);
        Assert.Contains("商店", text);
        Assert.Contains("手牌", text);
        Assert.Contains("战场", text);
        Assert.Contains("金币：未知", text);
        Assert.Contains("不会发送输入", text);
        Assert.Contains("判断依据", text);
    }

    [Fact]
    public void BatchReport_SummarizesAllRecognizedScreenshots()
    {
        var quality = new FrameQualityResult(
            "sample.png",
            FrameQuality.Good,
            "ok",
            new FrameQualityMetrics(1920, 1080, 100, 20, 0.1, 0.1, 0.05, 1));
        var report = ScreenshotRecognitionReport.Empty(1920, 1080, quality, "sample.png");
        var batch = new ScreenshotRecognitionBatchReport(
            "manifest.json",
            "commit",
            DateTimeOffset.UnixEpoch,
            [report]);

        var text = batch.ToText();

        Assert.Contains("识别批次：1 张", text);
        Assert.Contains("Shopping", text);
        Assert.Contains("sample.png", batch.ToJson());
    }
}
