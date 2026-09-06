using BattlegroundsVisionAgent.Core.Configuration;
using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Core.Rules;
using BattlegroundsVisionAgent.Replay;
using BattlegroundsVisionAgent.App.ViewModels;
using BattlegroundsVisionAgent.App.Views;
using BattlegroundsVisionAgent.Vision.Catalog;

namespace BattlegroundsVisionAgent.Replay.Tests;

public sealed class ObservationModeTests
{
    [Fact]
    public void ObservationMode_RecordsPlannerActionWithoutSendingInput()
    {
        var input = new SpyInputExecutor();
        var recorder = new ActionPlanRecorder();
        var harness = new TestObservationHarness(new AutomationPlanner(), recorder, input, observationMode: true);

        var planned = harness.Tick(CreateShopWithTarget());

        Assert.IsType<BuyAction>(planned);
        Assert.Empty(input.ExecutedActions);
        Assert.IsType<BuyAction>(Assert.Single(recorder.Actions));
    }

    [Fact]
    public void Recorder_ExposesAnImmutableSnapshotOfRecordedPlans()
    {
        var recorder = new ActionPlanRecorder();
        recorder.Record(new RefreshAction(4));

        var snapshot = recorder.Actions;
        recorder.Record(new RefreshAction(5));

        Assert.Single(snapshot);
        Assert.IsAssignableFrom<IReadOnlyList<AutomationAction>>(snapshot);
        Assert.False(snapshot is List<AutomationAction>);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("unlimited")]
    public void InvalidPurchaseLimit_DisablesStartAndExposesValidationMessage(string invalidLimit)
    {
        var viewModel = new MainViewModel();
        viewModel.LoadCatalog([new CardCatalogEntry("CARD_A", "测试卡", 2, "test.png")]);
        var card = Assert.Single(viewModel.CatalogCards);
        card.IsTargeted = true;
        card.PurchaseLimitText = invalidLimit;

        Assert.False(card.IsPurchaseLimitValid);
        Assert.False(string.IsNullOrWhiteSpace(card.PurchaseLimitValidationMessage));
        Assert.False(viewModel.StartCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("不限")]
    [InlineData("3")]
    public void ValidPurchaseLimit_AllowsObservationStart(string limit)
    {
        var viewModel = new MainViewModel();
        viewModel.LoadCatalog([new CardCatalogEntry("CARD_A", "测试卡", 2, "test.png")]);
        var card = Assert.Single(viewModel.CatalogCards);
        card.IsTargeted = true;
        card.PurchaseLimitText = limit;

        Assert.True(card.IsPurchaseLimitValid);
        Assert.True(viewModel.StartCommand.CanExecute(null));
    }

    [Fact]
    public void SellDecision_ConfirmationCannotBeOverwrittenBySubsequentKeepStillEvents()
    {
        var state = new SellConfirmationState();

        state.ConfirmSell();
        state.KeepStill();

        Assert.Equal(SellConfirmationResult.ConfirmSell, state.Result);
    }

    private static GameSnapshot CreateShopWithTarget() => new(
        layoutVersion: 3,
        confidence: 0.99,
        capturedAt: DateTimeOffset.UnixEpoch,
        gamePhase: GamePhase.Shopping,
        gold: 10,
        tavernTier: 2,
        shop: [Card("CARD_A", CardZone.Shop, 0)],
        hand: [],
        board: [],
        discoverOptions: [],
        handCapacity: 10,
        boardCapacity: 7,
        hasPendingTripleReward: false,
        hasUnknownBlockingUi: false);

    private static CardObservation Card(string cardId, CardZone zone, int slot) =>
        new(cardId, zone, slot, false, new NormalizedRect(0, 0, 0.1, 0.1), 0.99);

    private sealed class TestObservationHarness(
        AutomationPlanner planner,
        ActionPlanRecorder recorder,
        SpyInputExecutor input,
        bool observationMode)
    {
        private readonly AppSettings _settings = AppSettings.CreateDefault() with
        {
            Rules = [new CardRule("CARD_A", PurchaseLimit.Unlimited, CardDisposition.Keep,
                CardDisposition.Keep, 1, false)]
        };

        public AutomationAction Tick(GameSnapshot snapshot)
        {
            var action = planner.Plan(snapshot, _settings);
            if (observationMode)
            {
                recorder.Record(action);
            }
            else
            {
                input.Execute(action);
            }

            return action;
        }
    }

    private sealed class SpyInputExecutor
    {
        public List<AutomationAction> ExecutedActions { get; } = [];

        public void Execute(AutomationAction action) => ExecutedActions.Add(action);
    }
}
