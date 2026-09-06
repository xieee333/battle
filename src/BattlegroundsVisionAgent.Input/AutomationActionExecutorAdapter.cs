using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Core.Runtime;

namespace BattlegroundsVisionAgent.Input;

/// <summary>
/// Bridges the input safety layer to the dependency-free Core coordinator.
/// </summary>
public sealed class AutomationActionExecutorAdapter(IInputExecutor inputExecutor) : IAutomationActionExecutor
{
    private readonly IInputExecutor _inputExecutor = inputExecutor
        ?? throw new ArgumentNullException(nameof(inputExecutor));

    public async Task<AutomationExecutionResult> ExecuteAsync(
        AutomationAction action,
        GameSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var result = await _inputExecutor.ExecuteAsync(action, snapshot, cancellationToken)
            .ConfigureAwait(false);
        return new(
            result.Sent,
            result.RequiresUserConfirmation,
            result.Detail ?? result.Reason.ToString());
    }
}
