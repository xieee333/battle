using BattlegroundsVisionAgent.Core.Configuration;
using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Core.Rules;
using BattlegroundsVisionAgent.Core.Runtime;

namespace BattlegroundsVisionAgent.Core.Tests;

public sealed class AutomationCoordinatorTests
{
    [Fact]
    public async Task TickAsync_ReplansAfterLowRiskVerificationFailure()
    {
        var source = new QueueSource(
        [
            SnapshotFactory.Shopping(layoutVersion: 1, shop: [SnapshotFactory.Card("CARD_A", CardZone.Shop)]),
            SnapshotFactory.Shopping(layoutVersion: 2, shop: [SnapshotFactory.Card("CARD_A", CardZone.Shop)]),
            SnapshotFactory.Shopping(layoutVersion: 3, shop: [], hand: [SnapshotFactory.Card("CARD_A", CardZone.Hand)])
        ]);
        var executor = new SuccessfulExecutor();
        var settings = SnapshotFactory.Settings(
            new CardRule("CARD_A", PurchaseLimit.Unlimited, CardDisposition.Keep,
                CardDisposition.Keep, 1, false));
        var coordinator = new AutomationCoordinator(
            source,
            new AutomationPlanner(),
            new ActionVerifier(),
            () => settings,
            new RunState(),
            executor,
            options: new AutomationCoordinatorOptions
            {
                ObservationMode = false,
                MaxLowRiskRecoveries = 1,
                TickInterval = TimeSpan.Zero
            });

        var result = await coordinator.TickAsync(CancellationToken.None);

        Assert.True(result.Verified);
        Assert.Equal(1, result.RecoveryAttempt);
        Assert.Equal(2, executor.Actions.Count);
        Assert.Equal(1, coordinator.PlanningContext.SuccessfulPurchaseCounts["CARD_A"]);
    }

    [Fact]
    public async Task TickAsync_DoesNotRetryAnUnverifiedSell()
    {
        var source = new QueueSource(
        [
            SnapshotFactory.Shopping(layoutVersion: 1,
                hand: [SnapshotFactory.Card("HAND_KEEP", CardZone.Hand)],
                board: [SnapshotFactory.Card("CARD_A", CardZone.Board, 0)],
                handCapacity: 2),
            SnapshotFactory.Shopping(layoutVersion: 2,
                hand: [SnapshotFactory.Card("HAND_KEEP", CardZone.Hand)],
                board: [SnapshotFactory.Card("CARD_A", CardZone.Board, 0)],
                handCapacity: 2)
        ]);
        var executor = new SuccessfulExecutor();
        var settings = SnapshotFactory.Settings(SnapshotFactory.PlayThenSell("CARD_A"));
        var coordinator = new AutomationCoordinator(
            source,
            new AutomationPlanner(),
            new ActionVerifier(),
            () => settings,
            new RunState(),
            executor,
            options: new AutomationCoordinatorOptions { ObservationMode = false, TickInterval = TimeSpan.Zero });

        var result = await coordinator.TickAsync(CancellationToken.None);

        Assert.False(result.Verified);
        Assert.True(result.RequiresUserConfirmation);
        Assert.Single(executor.Actions);
    }

    [Fact]
    public async Task TickAsync_EmergencyStopPreventsExecution()
    {
        var source = new QueueSource(
        [
            SnapshotFactory.Shopping(layoutVersion: 1,
                shop: [SnapshotFactory.Card("CARD_A", CardZone.Shop)])
        ]);
        var executor = new SuccessfulExecutor();
        var runState = new RunState();
        runState.EmergencyStop();
        var settings = SnapshotFactory.Settings(
            new CardRule("CARD_A", PurchaseLimit.Unlimited, CardDisposition.Keep,
                CardDisposition.Keep, 1, false));
        var coordinator = new AutomationCoordinator(
            source,
            new AutomationPlanner(),
            new ActionVerifier(),
            () => settings,
            runState,
            executor,
            options: new AutomationCoordinatorOptions { ObservationMode = false, TickInterval = TimeSpan.Zero });

        var result = await coordinator.TickAsync(CancellationToken.None);

        Assert.IsType<StopAction>(result.Action);
        Assert.False(result.Sent);
        Assert.Empty(executor.Actions);
    }

    private sealed class QueueSource(IReadOnlyList<GameSnapshot> snapshots) : IAutomationSnapshotSource
    {
        private readonly Queue<GameSnapshot> _snapshots = new(snapshots);

        public Task<GameSnapshot> CaptureStableAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_snapshots.Dequeue());
        }
    }

    private sealed class SuccessfulExecutor : IAutomationActionExecutor
    {
        public List<AutomationAction> Actions { get; } = [];

        public Task<AutomationExecutionResult> ExecuteAsync(
            AutomationAction action,
            GameSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Actions.Add(action);
            return Task.FromResult(AutomationExecutionResult.Succeeded);
        }
    }
}
