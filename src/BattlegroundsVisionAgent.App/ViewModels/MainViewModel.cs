using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using BattlegroundsVisionAgent.App.Runtime;
using BattlegroundsVisionAgent.Core.Configuration;
using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Core.Rules;
using BattlegroundsVisionAgent.Core.Runtime;
using BattlegroundsVisionAgent.Vision.Catalog;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CoreRunStatus = BattlegroundsVisionAgent.Core.Runtime.RunStatus;

namespace BattlegroundsVisionAgent.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AutomationPlanner _planner;
    private AppSettings _settings;
    private readonly CardCatalog _cardCatalog;
    private readonly CardCatalogUpdater _catalogUpdater;
    private readonly BlizzardCatalogSyncService _officialCatalogSync;
    private readonly AutomationRuntimeSession _automationRuntime;
    private readonly string _catalogDirectory;
    private readonly RunState _runState;
    private readonly SynchronizationContext? _uiContext;

    public MainViewModel(
        AppSettings? settings = null,
        AutomationPlanner? planner = null,
        CardCatalog? cardCatalog = null,
        CardCatalogUpdater? catalogUpdater = null,
        BlizzardCatalogSyncService? officialCatalogSync = null,
        AutomationRuntimeSession? automationRuntime = null,
        RunState? runState = null)
    {
        _settings = settings ?? AppSettings.CreateDefault();
        _planner = planner ?? new AutomationPlanner();
        _cardCatalog = cardCatalog ?? new CardCatalog(Path.Combine(AppContext.BaseDirectory, "data", "catalog", "catalog.db"));
        _catalogUpdater = catalogUpdater ?? new CardCatalogUpdater();
        _officialCatalogSync = officialCatalogSync ?? new BlizzardCatalogSyncService();
        _catalogDirectory = Path.GetDirectoryName(Path.GetFullPath(_cardCatalog.DatabasePath))!;
        _runState = runState ?? new RunState(CoreRunStatus.Paused);
        _runState.StatusChanged += OnRunStateChanged;
        _uiContext = SynchronizationContext.Current;
        _automationRuntime = automationRuntime ?? new AutomationRuntimeSession(
            _cardCatalog.DatabasePath,
            _planner,
            () => Settings,
            _runState);
        _automationRuntime.TickCompleted += OnRuntimeTickCompleted;
        MinimumGold = _settings.MinimumGold;
        ReservedHandSlots = _settings.ReservedHandSlots;
        ObservationMode = _settings.ObservationMode;

        FilteredCatalogCards = CollectionViewSource.GetDefaultView(CatalogCards);
        FilteredCatalogCards.Filter = MatchesSearch;
        _cardCatalog.Initialize();
        LoadCatalogFromDatabase();
    }

    public ObservableCollection<CardRuleEditorViewModel> CatalogCards { get; } = [];
    public ICollectionView FilteredCatalogCards { get; }
    public IReadOnlyList<CardDisposition> CardActions { get; } = Enum.GetValues<CardDisposition>();
    public ObservableCollection<int> TierFilters { get; } = [0];

    [ObservableProperty]
    private int _selectedTier;

    [ObservableProperty]
    private bool _tierDescending;

    public string TierSortText => TierDescending ? "本数 ↓" : "本数 ↑";
    public string FilterResultText => $"显示 {FilteredCatalogCards.Cast<object>().Count()} / {CatalogCards.Count} 张 · {(TierDescending ? "高本 → 低本" : "低本 → 高本")}";

    [RelayCommand]
    private void ToggleTierSort() => TierDescending = !TierDescending;

    partial void OnTierDescendingChanged(bool value)
    {
        OnPropertyChanged(nameof(TierSortText));
        RefreshCardFilter();
    }

    partial void OnSelectedTierChanged(int value) => RefreshCardFilter();

    private void RefreshCardFilter()
    {
        using (FilteredCatalogCards.DeferRefresh())
        {
            FilteredCatalogCards.SortDescriptions.Clear();
            FilteredCatalogCards.SortDescriptions.Add(new SortDescription(nameof(CardRuleEditorViewModel.Tier),
                TierDescending ? ListSortDirection.Descending : ListSortDirection.Ascending));
            FilteredCatalogCards.SortDescriptions.Add(new SortDescription(nameof(CardRuleEditorViewModel.NameZhCn), ListSortDirection.Ascending));
        }
        FilteredCatalogCards.Refresh();
        OnPropertyChanged(nameof(FilterResultText));
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _cardSearchText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _observationMode;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private int _minimumGold;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private int _reservedHandSlots = 2;

    [ObservableProperty]
    private CardRuleEditorViewModel? _selectedCard;

    [ObservableProperty]
    private string _runStatus = "未加载卡库";

    [ObservableProperty]
    private string _latestPlannedAction = "等待识别画面";

    [ObservableProperty]
    private string _catalogVersionText = "未安装";

    [ObservableProperty]
    private string _catalogOperationStatus = "等待本地卡库";

    public bool IsCatalogLoaded => CatalogCards.Count > 0;
    public bool HasRules => CatalogCards.Any(card => card.IsTargeted);
    public bool HasValidTargetRules => CatalogCards.Where(card => card.IsTargeted).All(card => card.IsPurchaseLimitValid);
    public bool HasSelectedCard => SelectedCard is not null;
    public bool CanStartExecution => IsCatalogLoaded && HasRules && HasValidTargetRules
        && !ObservationMode && MinimumGold >= 0 && ReservedHandSlots is >= 0 and <= 10;
    public string CatalogStatusText => IsCatalogLoaded
        ? $"{CatalogCards.Count} 张卡牌 · {CatalogVersionText}"
        : CatalogOperationStatus;
    public string ExecutionStatusText => !IsCatalogLoaded
        ? "等待识别卡库"
        : !HasRules
            ? "请选择目标牌"
            : !HasValidTargetRules
                ? "存在无效规则"
                : ObservationMode
                    ? "观察模式：只记录计划"
                    : MinimumGold < 0 || ReservedHandSlots is < 0 or > 10
                        ? "安全参数无效"
                        : "可进入真实执行";

    public AppSettings Settings => BuildSettings();

    public RunState RunState => _runState;

    public void LoadCatalog(IEnumerable<CardCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var currentRules = _settings.Rules.ToDictionary(rule => rule.CardId, StringComparer.Ordinal);
        CatalogCards.Clear();

        foreach (var entry in entries.OrderBy(entry => entry.Tier).ThenBy(entry => entry.NameZhCn, StringComparer.Ordinal))
        {
            currentRules.TryGetValue(entry.CardId, out var rule);
            var item = new CardRuleEditorViewModel(entry, rule, OnRuleChanged, _catalogDirectory);
            CatalogCards.Add(item);
        }

        var previousTier = SelectedTier;
        TierFilters.Clear();
        TierFilters.Add(0);
        foreach (var tier in CatalogCards.Select(card => card.Tier).Where(tier => tier > 0).Distinct().Order())
            TierFilters.Add(tier);
        SelectedTier = TierFilters.Contains(previousTier) ? previousTier : 0;
        RefreshCardFilter();
        NotifyRuleStateChanged();
    }

    public async Task<CatalogUpdateResult> UpdateCatalogAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        CatalogOperationStatus = "正在校验卡库更新……";
        OnPropertyChanged(nameof(CatalogStatusText));
        try
        {
            var result = await _catalogUpdater.UpdateFromPackageAsync(_catalogDirectory, packagePath, cancellationToken);
            LoadCatalogFromDatabase();
            CatalogOperationStatus = result.Applied
                ? $"卡库已更新至 {result.Version}"
                : $"卡库已是 {result.Version}，无需更新";
            RunStatus = result.Applied ? "卡库更新完成" : "卡库无需更新";
            OnPropertyChanged(nameof(CatalogStatusText));
            return result;
        }
        catch (Exception exception)
        {
            CatalogOperationStatus = $"卡库更新失败：{exception.Message}";
            RunStatus = "卡库更新失败";
            OnPropertyChanged(nameof(CatalogStatusText));
            throw;
        }
    }

    public async Task<CatalogUpdateResult> SyncOfficialCatalogAsync(
        OfficialCatalogSyncOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        CatalogOperationStatus = "正在从暴雪官网读取卡库……";
        OnPropertyChanged(nameof(CatalogStatusText));
        try
        {
            _settings = BuildSettings();
            _runState.Pause();
            await _automationRuntime.StopAsync();
            var result = await _officialCatalogSync.SyncChinaAsync(
                _catalogDirectory, cancellationToken);
            LoadCatalogFromDatabase();
            CatalogOperationStatus = result.Applied
                ? $"官网卡库已同步至 {result.Version}"
                : "官网卡库没有新变化";
            RunStatus = result.Applied ? "官网卡库同步完成" : "官网卡库无需更新";
            OnPropertyChanged(nameof(CatalogStatusText));
            return result;
        }
        catch (Exception exception)
        {
            CatalogOperationStatus = $"官网卡库同步失败：{exception.Message}";
            RunStatus = "官网卡库同步失败";
            OnPropertyChanged(nameof(CatalogStatusText));
            throw;
        }
    }

    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        MinimumGold = settings.MinimumGold;
        ReservedHandSlots = settings.ReservedHandSlots;
        // Starting a new app session must always be read-only.  Execution can
        // still be enabled explicitly during this session, but a persisted
        // flag must never silently re-enable input after a restart.
        ObservationMode = true;
        LoadCatalogFromDatabase();
    }

    public Task StopRuntimeAsync() => _automationRuntime.StopAsync();

    public async ValueTask DisposeRuntimeAsync()
    {
        _automationRuntime.TickCompleted -= OnRuntimeTickCompleted;
        await _automationRuntime.DisposeAsync();
    }

    private void LoadCatalogFromDatabase()
    {
        var snapshot = _cardCatalog.ReadSnapshot();
        LoadCatalog(snapshot.Entries);
        if (snapshot.Metadata is null)
        {
            CatalogVersionText = "未安装版本";
            CatalogOperationStatus = "等待导入卡库更新包";
        }
        else
        {
            CatalogVersionText = $"v{snapshot.Metadata.Version}";
            CatalogOperationStatus = $"已加载 · 更新于 {snapshot.Metadata.UpdatedAt:yyyy-MM-dd HH:mm}";
        }
        OnPropertyChanged(nameof(CatalogStatusText));
    }

    public AutomationAction PlanForObservation(GameSnapshot snapshot)
    {
        var action = _planner.Plan(snapshot, BuildSettings());
        LatestPlannedAction = DescribeAction(action);
        return action;
    }

    partial void OnCardSearchTextChanged(string value) => RefreshCardFilter();

    partial void OnObservationModeChanged(bool value) => NotifyRuleStateChanged();

    partial void OnSelectedCardChanged(CardRuleEditorViewModel? value) =>
        OnPropertyChanged(nameof(HasSelectedCard));

    partial void OnMinimumGoldChanged(int value) => NotifyRuleStateChanged();

    partial void OnReservedHandSlotsChanged(int value) => NotifyRuleStateChanged();

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        _runState.Continue();
        RunStatus = ObservationMode ? "观察中" : "执行准备中";
        LatestPlannedAction = "等待稳定画面";
        try
        {
            await _automationRuntime.StartAsync(ObservationMode);
        }
        catch (Exception exception)
        {
            _runState.Pause();
            RunStatus = "视觉运行未启动";
            LatestPlannedAction = exception.Message;
        }
    }

    [RelayCommand]
    private async Task Pause()
    {
        _runState.Pause();
        await _automationRuntime.StopAsync();
        RunStatus = "已暂停";
    }

    [RelayCommand]
    private async Task Stop()
    {
        _runState.EmergencyStop();
        await _automationRuntime.StopAsync();
        RunStatus = "紧急停止";
        LatestPlannedAction = "未执行任何输入";
    }

    private void OnRuntimeTickCompleted(AutomationTickResult result)
    {
        void Apply()
        {
            LatestPlannedAction = DescribeAction(result.Action);
            if (result.RequiresUserConfirmation)
            {
                _runState.Pause();
                RunStatus = "需要人工确认";
            }
            else if (result.Action is StopAction { Reason: "shopping-data-unknown" })
            {
                RunStatus = "购物阶段，等待数字/卡牌识别";
            }
            else if (result.Action is StopAction { Reason: "scene-not-actionable" })
            {
                RunStatus = "等待可操作阶段";
            }
            else if (result.Action is StopAction or PauseForUserAction || (result.Sent && !result.Verified))
            {
                _runState.Pause();
                RunStatus = result.Action is StopAction ? "识别阻断，已暂停" : "复核失败，已暂停";
            }
            else
            {
                RunStatus = ObservationMode ? "观察中" : "执行中";
            }
        }

        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
            Apply();
        else
            _uiContext.Post(_ => Apply(), null);
    }

    private void OnRunStateChanged(CoreRunStatus status)
    {
        if (status == CoreRunStatus.Paused)
            RunStatus = "已暂停";
        else if (status == CoreRunStatus.Running && RunStatus == "已暂停")
            RunStatus = "运行中";
        else if (status == CoreRunStatus.EmergencyStopped)
        {
            RunStatus = "紧急停止";
            LatestPlannedAction = "未执行任何输入";
        }
    }

    private bool CanStart() => IsCatalogLoaded && HasRules && HasValidTargetRules
        && MinimumGold >= 0 && ReservedHandSlots is >= 0 and <= 10;

    private bool MatchesSearch(object item) => item is CardRuleEditorViewModel card
        && (SelectedTier == 0 || card.Tier == SelectedTier)
        && (string.IsNullOrWhiteSpace(CardSearchText)
            || card.NameZhCn.Contains(CardSearchText, StringComparison.OrdinalIgnoreCase)
            || card.CardId.Contains(CardSearchText, StringComparison.OrdinalIgnoreCase));

    private void OnRuleChanged(CardRuleEditorViewModel _) => NotifyRuleStateChanged();

    private void NotifyRuleStateChanged()
    {
        OnPropertyChanged(nameof(IsCatalogLoaded));
        OnPropertyChanged(nameof(HasRules));
        OnPropertyChanged(nameof(HasValidTargetRules));
        OnPropertyChanged(nameof(CanStartExecution));
        OnPropertyChanged(nameof(CatalogStatusText));
        OnPropertyChanged(nameof(ExecutionStatusText));
        StartCommand.NotifyCanExecuteChanged();
    }

    private AppSettings BuildSettings()
    {
        var rules = CatalogCards.Where(card => card.IsTargeted).Select(card => card.ToRule()).ToArray();
        _settings = _settings with
        {
            MinimumGold = Math.Max(0, MinimumGold),
            ReservedHandSlots = Math.Clamp(ReservedHandSlots, 0, 10),
            ObservationMode = ObservationMode,
            Rules = rules
        };
        return _settings;
    }

    private static string DescribeAction(AutomationAction action) => action switch
    {
        BuyAction buy => $"计划购买：{buy.CardId}",
        RefreshAction => "计划刷新酒馆",
        PlayAction play => $"计划上场：{play.CardId}",
        SellAction sell => $"计划卖出：{sell.CardId}",
        ChooseDiscoverAction choose => $"计划发现：{choose.CardId}",
        PauseForUserAction pause => $"等待确认：{pause.Reason}",
        StopAction { Reason: "shopping-data-unknown" } => "购物阶段已识别，等待数字/卡牌模板",
        StopAction { Reason: "scene-not-actionable" } => "等待可操作阶段",
        StopAction stop => $"停止：{stop.Reason}",
        NoneAction none => $"等待：{none.Reason}",
        _ => "等待"
    };
}

public sealed partial class CardRuleEditorViewModel : ObservableObject
{
    private readonly Action<CardRuleEditorViewModel> _onChanged;

    public CardRuleEditorViewModel(CardCatalogEntry entry, CardRule? rule, Action<CardRuleEditorViewModel> onChanged, string? catalogDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        CardId = entry.CardId;
        NameZhCn = entry.NameZhCn;
        Tier = entry.Tier;
        if (catalogDirectory is not null && !string.IsNullOrWhiteSpace(entry.ImagePath))
        {
            try
            {
                var path = Path.GetFullPath(Path.Combine(catalogDirectory, entry.ImagePath));
                if (File.Exists(path))
                {
                    using var stream = File.OpenRead(path);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 240;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    CardImage = bitmap;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.IO.FileFormatException)
            {
                // A missing or corrupt thumbnail must not prevent editing card rules.
            }
        }
        IsTargeted = rule is not null;
        PurchaseLimitText = rule?.PurchaseLimit.Count?.ToString() ?? "不限";
        NormalAction = rule?.NormalAction ?? CardDisposition.Keep;
        TripleAction = rule?.TripleAction ?? CardDisposition.Keep;
        DiscoverPriority = rule?.DiscoverPriority ?? 99;
        ProtectedOnBoard = rule?.ProtectedOnBoard ?? false;
    }

    public string CardId { get; }
    public string NameZhCn { get; }
    public int Tier { get; }
    public BitmapImage? CardImage { get; }

    [ObservableProperty]
    private bool _isTargeted;

    [ObservableProperty]
    private string _purchaseLimitText = "不限";

    public bool IsPurchaseLimitValid => TryParsePurchaseLimit(PurchaseLimitText, out _, out _);

    public string PurchaseLimitValidationMessage => TryParsePurchaseLimit(PurchaseLimitText, out _, out var message)
        ? string.Empty
        : message;

    [ObservableProperty]
    private CardDisposition _normalAction;

    [ObservableProperty]
    private CardDisposition _tripleAction;

    [ObservableProperty]
    private int _discoverPriority;

    [ObservableProperty]
    private bool _protectedOnBoard;

    public CardRule ToRule()
    {
        if (!TryParsePurchaseLimit(PurchaseLimitText, out var purchaseLimit, out var message))
        {
            throw new InvalidOperationException(message);
        }

        return new CardRule(
            CardId,
            purchaseLimit,
            NormalAction,
            TripleAction,
            Math.Max(0, DiscoverPriority),
            ProtectedOnBoard);
    }

    partial void OnIsTargetedChanged(bool value) => _onChanged(this);
    partial void OnPurchaseLimitTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsPurchaseLimitValid));
        OnPropertyChanged(nameof(PurchaseLimitValidationMessage));
        _onChanged(this);
    }
    partial void OnNormalActionChanged(CardDisposition value) => _onChanged(this);
    partial void OnTripleActionChanged(CardDisposition value) => _onChanged(this);
    partial void OnDiscoverPriorityChanged(int value) => _onChanged(this);
    partial void OnProtectedOnBoardChanged(bool value) => _onChanged(this);

    private static bool TryParsePurchaseLimit(string value, out PurchaseLimit purchaseLimit, out string message)
    {
        if (string.Equals(value, "不限", StringComparison.Ordinal))
        {
            purchaseLimit = PurchaseLimit.Unlimited;
            message = string.Empty;
            return true;
        }

        if (int.TryParse(value, out var count) && count > 0)
        {
            purchaseLimit = PurchaseLimit.Exactly(count);
            message = string.Empty;
            return true;
        }

        purchaseLimit = default;
        message = "购买上限只能填正整数或“不限”。";
        return false;
    }
}
