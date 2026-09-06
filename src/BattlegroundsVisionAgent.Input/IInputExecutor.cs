using BattlegroundsVisionAgent.Core.Domain;

namespace BattlegroundsVisionAgent.Input;

public interface IInputExecutor
{
    Task<InputExecutionResult> ExecuteAsync(
        AutomationAction action,
        GameSnapshot snapshot,
        CancellationToken cancellationToken);
}

public interface IWindowsInputBackend
{
    Task<InputBackendResult> SendAsync(InputBinding binding, CancellationToken cancellationToken);

    Task ReleaseAllAsync(CancellationToken cancellationToken);
}

public interface IGameFocusProbe
{
    bool IsHearthstoneForeground();
}

public enum InputExecutionReason
{
    Sent,
    SentWithMouseFallback,
    Paused,
    EmergencyStopped,
    Cancelled,
    GameNotForeground,
    SnapshotNotActionable,
    WrongGamePhase,
    LayoutVersionMismatch,
    UnsupportedAction,
    BindingUnavailable,
    BackendFailed,
    RequiresUserConfirmation
}

public sealed record InputExecutionResult(
    bool Sent,
    InputExecutionReason Reason,
    bool RequiresUserConfirmation = false,
    string? Detail = null);

public sealed record InputBackendResult(bool Succeeded, string? FailureReason = null)
{
    public static InputBackendResult Success { get; } = new(true);

    public static InputBackendResult Ok() => Success;

    public static InputBackendResult Failed(string failureReason) => new(false, failureReason);
}

public abstract record InputBinding;

public sealed record KeyboardBinding(KeyShortcut Shortcut) : InputBinding;

public sealed record MouseBinding(
    AutomationAction Action,
    NormalizedPoint PrimaryPoint,
    NormalizedPoint? DragDestination = null) : InputBinding;

public readonly record struct NormalizedPoint
{
    public NormalizedPoint(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x is < 0 or > 1 || y is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Normalized point must remain within 0..1.");
        }

        X = x;
        Y = y;
    }

    public double X { get; }

    public double Y { get; }
}
