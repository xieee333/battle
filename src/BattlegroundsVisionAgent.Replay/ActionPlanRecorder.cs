using System.Collections.ObjectModel;
using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Core.Runtime;

namespace BattlegroundsVisionAgent.Replay;

/// <summary>
/// Receives planned actions for observation and replay flows without sending input.
/// </summary>
public sealed class ActionPlanRecorder : IAutomationActionSink
{
    private readonly object _syncRoot = new();
    private readonly List<AutomationAction> _actions = [];

    public IReadOnlyList<AutomationAction> Actions
    {
        get
        {
            lock (_syncRoot)
            {
                return new ReadOnlyCollection<AutomationAction>(_actions.ToArray());
            }
        }
    }

    public void Record(AutomationAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        lock (_syncRoot)
        {
            _actions.Add(action);
        }
    }
}
