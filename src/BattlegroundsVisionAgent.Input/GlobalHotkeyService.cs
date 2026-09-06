using System.Runtime.InteropServices;
using BattlegroundsVisionAgent.Core.Runtime;

namespace BattlegroundsVisionAgent.Input;

public interface IHotkeyRegistrar
{
    bool Register(IntPtr windowHandle, int id, KeyShortcut shortcut);

    void Unregister(IntPtr windowHandle, int id);
}

public sealed class GlobalHotkeyService : IDisposable
{
    public const int WmHotkey = 0x0312;
    public const int PauseToggleId = 0x42564107;
    public const int EmergencyStopId = 0x42564108;

    private readonly RunState _runState;
    private readonly IHotkeyRegistrar _registrar;
    private readonly HashSet<int> _registeredIds = [];
    private IntPtr _windowHandle;
    private bool _disposed;

    public GlobalHotkeyService(RunState runState, IHotkeyRegistrar? registrar = null)
    {
        _runState = runState ?? throw new ArgumentNullException(nameof(runState));
        _registrar = registrar ?? new WindowsHotkeyRegistrar();
    }

    public void AttachWindow(IntPtr windowHandle)
    {
        ThrowIfDisposed();
        if (windowHandle == IntPtr.Zero)
        {
            throw new ArgumentException("A non-zero window handle is required for global hotkeys.", nameof(windowHandle));
        }

        if (_registeredIds.Count != 0)
        {
            throw new InvalidOperationException("Cannot change the hotkey window after registration.");
        }

        _windowHandle = windowHandle;
    }

    public void Start(string pauseToggleHotkey, string emergencyStopHotkey)
    {
        ThrowIfDisposed();
        if (_registeredIds.Count != 0)
        {
            throw new InvalidOperationException("Global hotkeys have already been registered.");
        }

        if (_windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Attach a WPF window before registering global hotkeys.");
        }

        if (!KeyShortcut.TryParse(pauseToggleHotkey, out var pauseShortcut))
        {
            throw new ArgumentException("The pause hotkey is invalid.", nameof(pauseToggleHotkey));
        }

        if (!KeyShortcut.TryParse(emergencyStopHotkey, out var emergencyShortcut))
        {
            throw new ArgumentException("The emergency-stop hotkey is invalid.", nameof(emergencyStopHotkey));
        }

        if (pauseShortcut.VirtualKey == emergencyShortcut.VirtualKey
            && pauseShortcut.Modifiers == emergencyShortcut.Modifiers)
        {
            throw new ArgumentException("Pause and emergency-stop hotkeys collide.", nameof(emergencyStopHotkey));
        }

        try
        {
            Register(PauseToggleId, pauseShortcut);
            Register(EmergencyStopId, emergencyShortcut);
        }
        catch
        {
            UnregisterAll();
            throw;
        }
    }

    public void Dispatch(int hotkeyId)
    {
        ThrowIfDisposed();
        if (!_registeredIds.Contains(hotkeyId))
        {
            return;
        }

        if (hotkeyId == PauseToggleId)
        {
            if (_runState.Status == RunStatus.Running)
            {
                _runState.Pause();
            }
            else if (_runState.Status == RunStatus.Paused)
            {
                _runState.Continue();
            }

            return;
        }

        if (hotkeyId == EmergencyStopId)
        {
            _runState.EmergencyStop();
        }
    }

    public bool HandleWindowMessage(int message, IntPtr wParam)
    {
        if (_disposed || message != WmHotkey)
        {
            return false;
        }

        var id = wParam.ToInt64();
        if (id is < int.MinValue or > int.MaxValue || !_registeredIds.Contains((int)id))
        {
            return false;
        }

        Dispatch((int)id);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            UnregisterAll();
        }
        finally
        {
            _disposed = true;
        }
    }

    private void Register(int id, KeyShortcut shortcut)
    {
        if (!_registrar.Register(_windowHandle, id, shortcut))
        {
            throw new InvalidOperationException($"The operating system rejected global hotkey {shortcut.DisplayText}.");
        }

        _registeredIds.Add(id);
    }

    private void UnregisterAll()
    {
        foreach (var id in _registeredIds.OrderBy(static id => id).ToArray())
        {
            try
            {
                _registrar.Unregister(_windowHandle, id);
            }
            finally
            {
                _registeredIds.Remove(id);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class WindowsHotkeyRegistrar : IHotkeyRegistrar
    {
        public bool Register(IntPtr windowHandle, int id, KeyShortcut shortcut) =>
            RegisterHotKey(windowHandle, id, shortcut.Modifiers, shortcut.VirtualKey);

        public void Unregister(IntPtr windowHandle, int id)
        {
            _ = UnregisterHotKey(windowHandle, id);
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
