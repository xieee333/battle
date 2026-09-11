using System.Collections.ObjectModel;
using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Recognition;
using BattlegroundsVisionAgent.Vision.Validation;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BattlegroundsVisionAgent.App.ViewModels;

public sealed class RecognitionValidationViewModel : ObservableObject
{
    public static IReadOnlyList<string> RecognitionStatuses { get; } = ["未确认", "正确", "错误", "空槽"];
    public static IReadOnlyList<string> PositionStatuses { get; } = ["未确认", "正确", "错误"];

    public ObservableCollection<RecognitionValidationRowViewModel> Cards { get; } = [];
    public ObservableCollection<string> Recommendations { get; } = [];

    public SnapshotRecognitionResult? Result { get; private set; }
    public string ImagePath { get; private set; } = "尚未导入截图";
    public string SummaryText { get; private set; } = "尚未识别";
    public string ZoneSummaryText { get; private set; } = "等待识别";
    public string SafetyText { get; private set; } = "导入截图后开始离线识别";
    public string ResultStatus { get; private set; } = "等待截图";
    public string CatalogStatusText { get; private set; } = "尚未读取当前卡库";
    public string SampleStatusText { get; private set; } = "尚未读取历史正样本";
    public string OverallNote { get; set; } = string.Empty;

    public void SetStatus(string status)
    {
        ResultStatus = status;
        OnPropertyChanged(nameof(ResultStatus));
    }

    public void SetCatalogStatus(string catalogStatus, string sampleStatus)
    {
        CatalogStatusText = catalogStatus;
        SampleStatusText = sampleStatus;
        OnPropertyChanged(nameof(CatalogStatusText));
        OnPropertyChanged(nameof(SampleStatusText));
    }

    public void LoadResult(
        SnapshotRecognitionResult result,
        IReadOnlyDictionary<string, string> cardNames,
        string imagePath)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(cardNames);

        Result = result;
        ImagePath = string.IsNullOrWhiteSpace(imagePath) ? "当前内存截图" : imagePath;
        Cards.Clear();
        foreach (var card in result.Cards
                     .Where(card => card.Observation.IsOccupied
                         || card.Observation.CardZone is CardZone.Shop or CardZone.Discover)
                     .OrderBy(card => card.Observation.CardZone)
                     .ThenBy(card => card.SlotIndex))
        {
            var id = card.Observation.CardId;
            var predictedName = id == "UNKNOWN"
                ? "未识别"
                : cardNames.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name)
                    ? name
                    : id;
            Cards.Add(new RecognitionValidationRowViewModel(card, predictedName));
        }

        SummaryText = $"场景：{PhaseLabel(result.Scene.GamePhase)}（{result.Scene.Confidence:P0}）  ·  " +
                      $"金币：{FormatDigit(result.Gold)}  ·  酒馆等级：{FormatDigit(result.TavernTier)}  ·  " +
                      $"护甲：{FormatDigit(result.Armor)}";
        ZoneSummaryText = $"商店 {result.Snapshot.Shop.Count} 张  ·  手牌 {result.Snapshot.Hand.Count}/{result.Snapshot.HandCapacity}  ·  " +
                          $"战场 {result.Snapshot.Board.Count}/{result.Snapshot.BoardCapacity}  ·  " +
                          $"发现 {result.Snapshot.DiscoverOptions.Count} 张";
        SafetyText = result.Snapshot.IsActionable
            ? "当前快照满足可行动条件，但校验窗口仍只读，不会点击游戏。"
            : "当前快照未达到可行动条件；结果只用于校验和采样，不会发送购买、刷新或升本输入。";
        ResultStatus = $"识别完成：{Cards.Count} 个卡槽，整体置信度 {result.Snapshot.Confidence:P0}";

        ReplaceRecommendations(RecognitionValidationAdvisor.BuildRecommendations(result));
        NotifySummaryChanged();
    }

    public void RebuildRecommendations()
    {
        if (Result is null)
            return;
        ReplaceRecommendations(RecognitionValidationAdvisor.BuildRecommendations(
            Result,
            Cards.Select(card => card.ToFeedback()).ToArray()));
        OnPropertyChanged(nameof(ResultStatus));
    }

    public RecognitionValidationFeedback CreateFeedback(string catalogVersion)
    {
        if (Result is null)
            throw new InvalidOperationException("尚未完成识别，无法保存反馈。");

        RebuildRecommendations();
        return new RecognitionValidationFeedback(
            SchemaVersion: 1,
            CreatedAt: DateTimeOffset.Now,
            ImagePath,
            Result.Scene.GamePhase,
            Result.Scene.Confidence,
            Result.Gold.Value,
            Result.Gold.Confidence,
            Result.TavernTier.Value,
            Result.TavernTier.Confidence,
            Result.Armor?.Value,
            Result.Snapshot.IsActionable,
            Cards.Select(card => card.ToFeedback()).ToArray(),
            Recommendations.ToArray(),
            OverallNote,
            catalogVersion);
    }

    private void ReplaceRecommendations(IEnumerable<string> recommendations)
    {
        Recommendations.Clear();
        foreach (var recommendation in recommendations)
            Recommendations.Add(recommendation);
    }

    private void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(ImagePath));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(ZoneSummaryText));
        OnPropertyChanged(nameof(SafetyText));
        OnPropertyChanged(nameof(ResultStatus));
    }

    private static string FormatDigit(DigitRecognition? digit) =>
        digit is { IsKnown: true, Value: not null } ? $"{digit.Value}（{digit.Confidence:P0}）" : "未知";

    private static string PhaseLabel(GamePhase phase) => phase switch
    {
        GamePhase.Shopping => "购物阶段",
        GamePhase.Combat => "战斗阶段",
        GamePhase.Discover => "发现阶段",
        _ => "未知阶段"
    };
}

public sealed class RecognitionValidationRowViewModel : ObservableObject
{
    private string _recognitionStatus = "未确认";
    private string _positionStatus = "未确认";
    private string _expectedText = string.Empty;
    private string _note = string.Empty;

    public RecognitionValidationRowViewModel(RecognizedCard card, string predictedName)
    {
        Observation = card.Observation;
        Bounds = card.Bounds;
        PredictedCardId = card.Observation.CardId;
        PredictedName = predictedName;
        ZoneLabel = ZoneToLabel(card.Observation.CardZone);
        SlotLabel = $"槽位 {card.SlotIndex + 1}";
        KindLabel = KindToLabel(card.Observation.Kind);
        ConfidenceText = $"{card.Confidence:P0}";
        GoldenText = card.Observation.IsGolden ? "金色" : "普通";
        KindAndGoldenText = $"{KindLabel} · {GoldenText}";
        IsUnknown = card.Observation.CardId == "UNKNOWN";
        IsLowConfidence = card.Confidence < 0.80;
    }

    public IReadOnlyList<string> RecognitionStatuses => RecognitionValidationViewModel.RecognitionStatuses;
    public IReadOnlyList<string> PositionStatuses => RecognitionValidationViewModel.PositionStatuses;
    public CardObservation Observation { get; }
    public NormalizedRect Bounds { get; }
    public string ZoneLabel { get; }
    public string SlotLabel { get; }
    public string PredictedCardId { get; }
    public string PredictedName { get; }
    public string KindLabel { get; }
    public string KindAndGoldenText { get; }
    public string ConfidenceText { get; }
    public string GoldenText { get; }
    public bool IsUnknown { get; }
    public bool IsLowConfidence { get; }

    public string RecognitionStatus
    {
        get => _recognitionStatus;
        set => SetProperty(ref _recognitionStatus, value);
    }

    public string PositionStatus
    {
        get => _positionStatus;
        set => SetProperty(ref _positionStatus, value);
    }

    public string ExpectedText
    {
        get => _expectedText;
        set => SetProperty(ref _expectedText, value);
    }

    public string Note
    {
        get => _note;
        set => SetProperty(ref _note, value);
    }

    public RecognitionValidationCardFeedback ToFeedback() => new(
        ZoneLabel,
        Observation.SlotIndex,
        PredictedCardId,
        PredictedName,
        Observation.Kind,
        Observation.IsGolden,
        Observation.Confidence,
        Bounds,
        RecognitionStatus,
        PositionStatus,
        ExpectedText,
        Note);

    private static string ZoneToLabel(CardZone zone) => zone switch
    {
        CardZone.Shop => "商店",
        CardZone.Hand => "手牌",
        CardZone.Board => "战场",
        CardZone.Discover => "发现",
        _ => zone.ToString()
    };

    private static string KindToLabel(CardKind kind) => kind switch
    {
        CardKind.Minion => "随从",
        CardKind.Spell => "法术",
        _ => "未知类型"
    };
}
