using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;

namespace BattlegroundsVisionAgent.Input;

public static class WindowsGameWindowLocator
{
    private static readonly string[] TitleMarkers = ["炉石传说", "Hearthstone"];

    public static IntPtr FindHearthstoneWindow()
    {
        IntPtr match = IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || GetWindowText(handle) is not { Length: > 0 } title)
                return true;
            if (TitleMarkers.Any(marker => title.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                GetWindowThreadProcessId(handle, out var processId);
                try
                {
                    using var process = Process.GetProcessById((int)processId);
                    if (!string.Equals(process.ProcessName, "Hearthstone", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    return true;
                }
                match = handle;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return match;
    }

    private static string? GetWindowText(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0)
            return null;
        var builder = new StringBuilder(length + 1);
        return GetWindowText(handle, builder, builder.Capacity) > 0 ? builder.ToString() : null;
    }

    private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);
}

public sealed class WindowsGameFocusProbe(IntPtr targetWindowHandle) : IGameFocusProbe
{
    public bool IsHearthstoneForeground()
    {
        if (targetWindowHandle == IntPtr.Zero)
            return false;
        var foreground = GetForegroundWindow();
        return foreground == targetWindowHandle || IsChild(targetWindowHandle, foreground);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr child);
}
