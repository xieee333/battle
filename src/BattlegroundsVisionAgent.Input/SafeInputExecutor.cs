using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Core.Runtime;

namespace BattlegroundsVisionAgent.Input;

public sealed class SafeInputExecutor : IInputExecutor, IAsyncDisposable
{
    private readonly IWindowsInputBackend _backend;
    private readonly RunState _runState;
    private readonly IGameFocusProbe _focusProbe;
    private readonly ActionBindingResolver _bindingResolver;
    private bool _disposed;

    public SafeInputExecutor(
        IWindowsInputBackend backend,
        RunState runState,
        IGameFocusProbe focusProbe,
        ActionBindingResolver bindingResolver)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _runState = runState ?? throw new ArgumentNullException(nameof(runState));
        _focusProbe = focusProbe ?? throw new ArgumentNullException(nameof(focusProbe));
        _bindingResolver = bindingResolver ?? throw new ArgumentNullException(nameof(bindingResolver));
        _runState.EmergencyStopRequested += ReleaseInputAfterEmergencyStop;
    }

    public async Task<InputExecutionResult> ExecuteAsync(
        AutomationAction action,
        GameSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(snapshot);

        var stateResult = CheckRunState();
        if (stateResult is not null)
        {
            return stateResult;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new InputExecutionResult(false, InputExecutionReason.Cancelled);
        }

        if (!_focusProbe.IsHearthstoneForeground())
        {
            return new InputExecutionResult(false, InputExecutionReason.GameNotForeground);
        }

        if (!HasExpectedPhase(action, snapshot.GamePhase))
        {
            return new InputExecutionResult(false, InputExecutionReason.WrongGamePhase);
        }

        if (!snapshot.IsActionable)
        {
            return new InputExecutionResult(false, InputExecutionReason.SnapshotNotActionable);
        }

        if (action.LayoutVersion != snapshot.LayoutVersion)
        {
            return new InputExecutionResult(false, InputExecutionReason.LayoutVersionMismatch);
        }

        var resolution = _bindingResolver.Resolve(action, snapshot);
        if (resolution.Binding is null)
        {
            var reason = ActionBindingResolver.TryGetActionKind(action, out _)
                ? InputExecutionReason.BindingUnavailable
                : InputExecutionReason.UnsupportedAction;
            return new InputExecutionResult(false, reason);
        }

        var initial = await _backend.SendAsync(resolution.Binding, cancellationToken).ConfigureAwait(false);
        if (initial.Succeeded)
        {
            return new InputExecutionResult(true, InputExecutionReason.Sent);
        }

        if (resolution.Binding is KeyboardBinding && action is SellAction)
        {
            return new InputExecutionResult(false, InputExecutionReason.RequiresUserConfirmation, true, initial.FailureReason);
        }

        if (resolution.Binding is KeyboardBinding && action is RefreshAction && resolution.AllowMouseFallback)
        {
            var mouseResolution = new ActionBindingResolver().Resolve(action, snapshot);
            if (mouseResolution.Binding is MouseBinding mouseBinding)
            {
                var fallback = await _backend.SendAsync(mouseBinding, cancellationToken).ConfigureAwait(false);
                return fallback.Succeeded
                    ? new InputExecutionResult(true, InputExecutionReason.SentWithMouseFallback)
                    : new InputExecutionResult(false, InputExecutionReason.BackendFailed, false, fallback.FailureReason);
            }
        }

        return new InputExecutionResult(false, InputExecutionReason.BackendFailed, false, initial.FailureReason);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runState.EmergencyStopRequested -= ReleaseInputAfterEmergencyStop;
        await _backend.ReleaseAllAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private InputExecutionResult? CheckRunState() => _runState.Status switch
    {
        RunStatus.Paused => new InputExecutionResult(false, InputExecutionReason.Paused),
        RunStatus.EmergencyStopped => new InputExecutionResult(false, InputExecutionReason.EmergencyStopped),
        _ => null
    };

    private static bool HasExpectedPhase(AutomationAction action, GamePhase gamePhase) => action switch
    {
        ChooseDiscoverAction => gamePhase == GamePhase.Discover,
        _ => gamePhase == GamePhase.Shopping
    };

    private void ReleaseInputAfterEmergencyStop()
    {
        try
        {
            _backend.ReleaseAllAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // The emergency state remains authoritative even when release reports a platform failure.
        }
    }
}
