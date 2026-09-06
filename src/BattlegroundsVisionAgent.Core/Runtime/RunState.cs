namespace BattlegroundsVisionAgent.Core.Runtime;

public enum RunStatus
{
    Running = 0,
    Paused = 1,
    EmergencyStopped = 2
}

public sealed class RunState
{
    private int _status;

    public RunState(RunStatus initialStatus = RunStatus.Running)
    {
        _status = (int)initialStatus;
    }

    public event Action? EmergencyStopRequested;
    public event Action<RunStatus>? StatusChanged;

    public RunStatus Status => (RunStatus)Volatile.Read(ref _status);

    public void Pause()
    {
        if (Status != RunStatus.EmergencyStopped)
        {
            if (Interlocked.Exchange(ref _status, (int)RunStatus.Paused) != (int)RunStatus.Paused)
                StatusChanged?.Invoke(RunStatus.Paused);
        }
    }

    public void Continue()
    {
        if (Status != RunStatus.EmergencyStopped)
        {
            if (Interlocked.Exchange(ref _status, (int)RunStatus.Running) != (int)RunStatus.Running)
                StatusChanged?.Invoke(RunStatus.Running);
        }
    }

    public void EmergencyStop()
    {
        if (Interlocked.Exchange(ref _status, (int)RunStatus.EmergencyStopped) != (int)RunStatus.EmergencyStopped)
        {
            StatusChanged?.Invoke(RunStatus.EmergencyStopped);
            EmergencyStopRequested?.Invoke();
        }
    }
}
