using System.Runtime.InteropServices;

namespace BattlegroundsVisionAgent.Input;

public sealed class WindowsInputBackend : IWindowsInputBackend, IDisposable
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseMove = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseAbsolute = 0x8000;
    private const uint MouseVirtualDesk = 0x4000;
    private const uint KeyUp = 0x0002;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IntPtr _targetWindowHandle;
    private readonly HashSet<ushort> _heldKeys = [];
    private bool _leftMouseDown;
    private bool _disposed;

    public WindowsInputBackend(IntPtr targetWindowHandle)
    {
        if (targetWindowHandle == IntPtr.Zero)
            throw new ArgumentException("A target game window handle is required for mouse input.", nameof(targetWindowHandle));
        _targetWindowHandle = targetWindowHandle;
    }

    public async Task<InputBackendResult> SendAsync(InputBinding binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return binding switch
            {
                KeyboardBinding keyboard => SendKeyboard(keyboard.Shortcut),
                MouseBinding mouse => SendMouse(mouse),
                _ => InputBackendResult.Failed("unsupported-binding")
            };
        }
        catch (OperationCanceledException)
        {
            return InputBackendResult.Failed("cancelled");
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException)
        {
            return InputBackendResult.Failed($"sendinput-failed:{exception.GetType().Name}");
        }
        finally
        {
            try
            {
                ReleaseAllCore();
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public async Task ReleaseAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ReleaseAllCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gate.Wait();
        try
        {
            ReleaseAllCore();
            _disposed = true;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private InputBackendResult SendKeyboard(KeyShortcut shortcut)
    {
        var modifierKeys = GetModifierKeys(shortcut.Modifiers);
        foreach (var key in modifierKeys)
        {
            SendKey(key, keyUp: false);
            _heldKeys.Add(key);
        }

        SendKey(shortcut.VirtualKey, keyUp: false);
        _heldKeys.Add(shortcut.VirtualKey);
        SendKey(shortcut.VirtualKey, keyUp: true);
        _heldKeys.Remove(shortcut.VirtualKey);
        return InputBackendResult.Ok();
    }

    private InputBackendResult SendMouse(MouseBinding binding)
    {
        SendMouseInput(binding.PrimaryPoint, MouseMove | MouseAbsolute | MouseVirtualDesk);
        SendMouseInput(binding.PrimaryPoint, MouseLeftDown | MouseAbsolute | MouseVirtualDesk);
        _leftMouseDown = true;
        if (binding.DragDestination is { } destination)
        {
            SendMouseInput(destination, MouseMove | MouseAbsolute | MouseVirtualDesk);
        }

        var releasePoint = binding.DragDestination ?? binding.PrimaryPoint;
        SendMouseInput(releasePoint, MouseLeftUp | MouseAbsolute | MouseVirtualDesk);
        _leftMouseDown = false;
        return InputBackendResult.Ok();
    }

    private void ReleaseAllCore()
    {
        if (_leftMouseDown)
        {
            SendMouseInput(new NormalizedPoint(0, 0), MouseLeftUp | MouseAbsolute | MouseVirtualDesk);
            _leftMouseDown = false;
        }

        foreach (var key in _heldKeys.ToArray())
        {
            SendKey(key, keyUp: true);
            _heldKeys.Remove(key);
        }
    }

    private static IEnumerable<ushort> GetModifierKeys(ushort modifiers)
    {
        if ((modifiers & 0x0001) != 0) yield return 0x12;
        if ((modifiers & 0x0002) != 0) yield return 0x11;
        if ((modifiers & 0x0004) != 0) yield return 0x10;
    }

    private static void SendKey(ushort virtualKey, bool keyUp)
    {
        var input = new INPUT
        {
            Type = InputKeyboard,
            Union = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKey = virtualKey, Flags = keyUp ? KeyUp : 0 } }
        };
        SendOrThrow(input);
    }

    private void SendMouseInput(NormalizedPoint point, uint flags)
    {
        if (!GetClientRect(_targetWindowHandle, out var clientRect))
            throw new ExternalException("Unable to map game-window coordinates to the desktop.", Marshal.GetLastWin32Error());

        var clientOrigin = new Point(clientRect.Left, clientRect.Top);
        if (!ClientToScreen(_targetWindowHandle, ref clientOrigin))
            throw new ExternalException("Unable to map game-window coordinates to the desktop.", Marshal.GetLastWin32Error());

        var clientWidth = clientRect.Right - clientRect.Left;
        var clientHeight = clientRect.Bottom - clientRect.Top;
        if (clientWidth <= 0 || clientHeight <= 0)
            throw new InvalidOperationException("The target game window has no usable client area.");

        var screenX = clientOrigin.X + (int)Math.Round(point.X * Math.Max(0, clientWidth - 1));
        var screenY = clientOrigin.Y + (int)Math.Round(point.Y * Math.Max(0, clientHeight - 1));
        var virtualLeft = GetSystemMetrics(SystemMetricVirtualLeft);
        var virtualTop = GetSystemMetrics(SystemMetricVirtualTop);
        var virtualWidth = Math.Max(1, GetSystemMetrics(SystemMetricVirtualWidth) - 1);
        var virtualHeight = Math.Max(1, GetSystemMetrics(SystemMetricVirtualHeight) - 1);
        var input = new INPUT
        {
            Type = InputMouse,
            Union = new InputUnion
            {
                Mouse = new MOUSEINPUT
                {
                    Dx = checked((int)Math.Round((screenX - virtualLeft) * 65535d / virtualWidth)),
                    Dy = checked((int)Math.Round((screenY - virtualTop) * 65535d / virtualHeight)),
                    Flags = flags
                }
            }
        };
        SendOrThrow(input);
    }

    private static void SendOrThrow(INPUT input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1)
        {
            throw new ExternalException("SendInput rejected the input event.", Marshal.GetLastWin32Error());
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;

        public Point(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    private const int SystemMetricVirtualLeft = 76;
    private const int SystemMetricVirtualTop = 77;
    private const int SystemMetricVirtualWidth = 78;
    private const int SystemMetricVirtualHeight = 79;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr handle, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr handle, ref Point point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
