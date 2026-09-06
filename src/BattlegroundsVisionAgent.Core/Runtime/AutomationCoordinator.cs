using BattlegroundsVisionAgent.Core.Configuration;
using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Core.Rules;

namespace BattlegroundsVisionAgent.Core.Runtime;

public interface IAutomationSnapshotSource
{
    Task<GameSnapshot> CaptureStableAsync(CancellationToken cancellationToken);
}

public interface IAutomationActionExecutor
{
    Task<AutomationExecutionResult> ExecuteAsync(
        AutomationAction action,
        GameSnapshot snapshot,
        CancellationToken cancellationToken);
}

public interface IAutomationActionSink
{
    void Record(AutomationAction action);
}

public sealed record AutomationExecutionResult(
    bool Sent,
    bool RequiresUserConfirmation = false,
    string? Detail = null)
{
    public static AutomationExecutionResult Succeeded { get; } = new(true);

    public static AutomationExecutionResult Failed(string detail, bool requiresUserConfirmation = false) =>
        new(false, requiresUserConfirmation, detail);
}

public sealed record AutomationCoordinatorOptions
{
    public bool ObservationMode { get; init; } = true;
    public int MaxLowRiskRecoveries { get; init; } = 2;
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public void Validate()
    {
        if (MaxLowRiskRecoveries < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxLowRiskRecoveries));
        if (TickInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(TickInterval));
    }
}

public sealed record AutomationTickResult(
    GameSnapshot BeforeSnapshot,
    AutomationAction Action,
    GameSnapshot? AfterSnapshot,
    bool Sent,
    bool Verified,
    int RecoveryAttempt,
    string? FailureReason = null,
    bool RequiresUserConfirmation = false);

public sealed class AutomationCoordinator
{
    private readonly IAutomationSnapshotSource _snapshotSource;
    private readonly AutomationPlanner _planner;
    private readonly ActionVerifier _actionVerifier;
    private readonly Func<AppSettings> _settingsProvider;
    private readonly RunState _runState;
    private readonly IAutomationActionExecutor? _executor;
    private readonly IAutomationActionSink? _actionSink;
    private readonly AutomationCoordinatorOptions _options;
    private PlanningContext _planningContext = PlanningContext.Empty;

    public AutomationCoordinator(
        IAutomationSnapshotSource snapshotSource,
        AutomationPlanner planner,
        ActionVerifier actionVerifier,
        Func<AppSettings> settingsProvider,
        RunState runState,
        IAutomationActionExecutor? executor = null,
        IAutomationActionSink? actionSink = null,
        AutomationCoordinatorOptions? options = null)
    {
        _snapshotSource = snapshotSource ?? throw new ArgumentNullException(nameof(snapshotSource));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _actionVerifier = actionVerifier ?? throw new ArgumentNullException(nameof(actionVerifier));
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        _runState = runState ?? throw new ArgumentNullException(nameof(runState));
        _executor = executor;
        _actionSink = actionSink;
        _options = options ?? new AutomationCoordinatorOptions();
        _options.Validate();

        if (!_options.ObservationMode && _executor is null)
            throw new ArgumentException("Execution mode requires an action executor.", nameof(executor));
    }

    public PlanningContext PlanningContext => _planningContext;

    public event Action<AutomationTickResult>? TickCompleted;

    public async Task<AutomationTickResult> TickAsync(CancellationToken cancellationToken)
    {
        var before = await _snapshotSource.CaptureStableAsync(cancellationToken).ConfigureAwait(false);
        for (var recoveryAttempt = 0; ; recoveryAttempt++)
        {
            var action = Plan(before);

            if (_options.ObservationMode)
            {
                _actionSink?.Record(action);
                return Complete(new AutomationTickResult(before, action, null, false, false, recoveryAttempt));
            }

            if (action is NoneAction or StopAction or PauseForUserAction)
            {
                return Complete(new AutomationTickResult(before, action, null, false, false, recoveryAttempt));
            }

            var execution = await _executor!.ExecuteAsync(action, before, cancellationToken).ConfigureAwait(false);
            if (!execution.Sent)
            {
                return Complete(new AutomationTickResult(
                    before,
                    action,
                    null,
                    false,
                    false,
                    recoveryAttempt,
                    execution.Detail ?? "input-not-sent",
                    execution.RequiresUserConfirmation));
            }

            var after = await _snapshotSource.CaptureStableAsync(cancellationToken).ConfigureAwait(false);
            var verified = _actionVerifier.Verify(action, before, after);
            if (verified)
            {
                UpdatePlanningContext(action);
                return Complete(new AutomationTickResult(before, action, after, true, true, recoveryAttempt));
            }

            var isHighRisk = action is SellAction;
            if (isHighRisk || recoveryAttempt >= _options.MaxLowRiskRecoveries)
            {
                return Complete(new AutomationTickResult(
                    before,
                    action,
                    after,
                    true,
                    false,
                    recoveryAttempt,
                    "action-verification-failed",
                    isHighRisk));
            }

            // The failed action may nevertheless have changed the scene. Replan from
            // the newly captured stable snapshot instead of reusing stale coordinates.
            before = after;
        }
    }

    private AutomationTickResult Complete(AutomationTickResult result)
    {
        TickCompleted?.Invoke(result);
        return result;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested
            && _runState.Status == RunStatus.Running)
        {
            var result = await TickAsync(cancellationToken).ConfigureAwait(false);
            if (IsPassiveSceneStop(result.Action))
            {
                if (_options.TickInterval > TimeSpan.Zero)
                    await Task.Delay(_options.TickInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (result.Action is StopAction or PauseForUserAction
                || (result.Sent && !result.Verified))
            {
                return;
            }

            if (_options.TickInterval > TimeSpan.Zero)
            {
                await Task.Delay(_options.TickInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsPassiveSceneStop(AutomationAction action) =>
        action is StopAction { Reason: "scene-not-actionable" or "shopping-data-unknown" };

    private AutomationAction Plan(GameSnapshot snapshot)
    {
        if (_runState.Status == RunStatus.EmergencyStopped)
            return new StopAction(snapshot.LayoutVersion, "emergency-stopped");
        if (_runState.Status == RunStatus.Paused)
            return new PauseForUserAction(snapshot.LayoutVersion, "paused", null);

        return _planner.Plan(snapshot, _settingsProvider(), _planningContext);
    }

    private void UpdatePlanningContext(AutomationAction action)
    {
        if (action is not BuyAction buy)
            return;

        var counts = _planningContext.SuccessfulPurchaseCounts.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        counts[buy.CardId] = counts.TryGetValue(buy.CardId, out var count) ? count + 1 : 1;
        _planningContext = new PlanningContext(counts);
    }
}
