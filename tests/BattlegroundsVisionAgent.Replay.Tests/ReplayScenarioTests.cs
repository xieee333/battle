using BattlegroundsVisionAgent.Core.Configuration;
using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Core.Rules;
using BattlegroundsVisionAgent.Core.Runtime;
using BattlegroundsVisionAgent.Replay;

namespace BattlegroundsVisionAgent.Replay.Tests;

public sealed class ReplayScenarioTests
{
    [Fact]
    public async Task TripleDiscoverSell_ReplansAfterEverySceneChange()
    {
        var scenario = ReplayScenario.Load("testdata/manifests/triple-discover-sell.json");
        var result = await ReplayRunner.RunAsync(scenario, CancellationToken.None);

        Assert.Equal(
            ["Buy:CARD_A", "Play:CARD_A:Golden", "Play:TRIPLE_REWARD",
                "ChooseDiscover:CARD_B", "Sell:CARD_A"],
            result.ActionLabels);
        Assert.True(result.MatchesExpectedActions);
        Assert.All(result.Steps, step =>
        {
            Assert.True(step.Verified);
            Assert.False(step.UsedStaleLayout);
            Assert.True(step.AfterLayoutVersion > step.BeforeLayoutVersion);
        });
    }

    [Fact]
    public async Task BoardShift_NeverUsesCoordinatesFromPreviousLayout()
    {
        var scenario = ReplayScenario.Load("testdata/manifests/board-shift.json");
        var result = await ReplayRunner.RunAsync(scenario, CancellationToken.None);

        var sell = Assert.IsType<SellAction>(Assert.Single(result.Actions));
        Assert.Equal(1, sell.BoardSlot);
        Assert.Equal(["Sell:CARD_SELL"], result.ActionLabels);
        Assert.DoesNotContain(result.Steps, step => step.UsedStaleLayout);
        Assert.All(result.Steps, step => Assert.True(step.Verified));
    }

    [Fact]
    public async Task Coordinator_ObservationModeRecordsPlanWithoutCallingExecutor()
    {
        var source = new QueueSnapshotSource([CreateShoppingSnapshot()]);
        var sink = new RecordingSink();
        var executor = new ThrowingExecutor();
        var coordinator = new AutomationCoordinator(
            source,
            new AutomationPlanner(),
            new ActionVerifier(),
            () => AppSettings.CreateDefault() with
            {
                Rules = [new CardRule("CARD_A", PurchaseLimit.Unlimited, CardDisposition.Keep,
                    CardDisposition.Keep, 1, false)]
            },
            new RunState(),
            executor,
            sink,
            new AutomationCoordinatorOptions { ObservationMode = true, TickInterval = TimeSpan.Zero });

        var result = await coordinator.TickAsync(CancellationToken.None);

        Assert.IsType<BuyAction>(result.Action);
        Assert.False(result.Sent);
        Assert.Empty(executor.Actions);
        Assert.IsType<BuyAction>(Assert.Single(sink.Actions));
    }

    private static GameSnapshot CreateShoppingSnapshot() => new(
        1,
        0.99,
        DateTimeOffset.UnixEpoch,
        GamePhase.Shopping,
        10,
        2,
        [new CardObservation("CARD_A", CardZone.Shop, 0, false, new NormalizedRect(0, 0, 0.1, 0.1), 0.99)],
        [],
        [],
        [],
        10,
        7,
        false,
        false);

    private sealed class QueueSnapshotSource(IReadOnlyList<GameSnapshot> snapshots) : IAutomationSnapshotSource
    {
        private readonly Queue<GameSnapshot> _snapshots = new(snapshots);

        public Task<GameSnapshot> CaptureStableAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_snapshots.Dequeue());
        }
    }

    private sealed class RecordingSink : IAutomationActionSink
    {
        public List<AutomationAction> Actions { get; } = [];

        public void Record(AutomationAction action) => Actions.Add(action);
    }

    private sealed class ThrowingExecutor : IAutomationActionExecutor
    {
        public List<AutomationAction> Actions { get; } = [];

        public Task<AutomationExecutionResult> ExecuteAsync(
            AutomationAction action,
            GameSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            Actions.Add(action);
            throw new InvalidOperationException("Observation mode must not execute input.");
        }
    }
}
