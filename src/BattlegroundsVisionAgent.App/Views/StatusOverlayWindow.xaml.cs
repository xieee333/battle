using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace BattlegroundsVisionAgent.App.Views;

public partial class StatusOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WmMouseActivate = 0x0021;
    private const int WmNcHitTest = 0x0084;
    private const int MaNoActivate = 3;
    private const int HtTransparent = -1;

    public StatusOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        SetExtendedStyle(handle, new IntPtr(GetExtendedStyle(handle).ToInt64() | WsExNoActivate));
        HwndSource.FromHwnd(handle)?.AddHook(WindowMessageHook);
    }

    private IntPtr WindowMessageHook(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmMouseActivate)
        {
            handled = true;
            return new IntPtr(MaNoActivate);
        }

        if (message == WmNcHitTest && !IsOverInteractiveControl(lParam))
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }

        return IntPtr.Zero;
    }

    private bool IsOverInteractiveControl(IntPtr lParam)
    {
        var raw = lParam.ToInt64();
        var screenPoint = new Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF));
        DependencyObject? current = InputHitTest(PointFromScreen(screenPoint)) as DependencyObject;
        while (current is not null)
        {
            if (current is Button)
                return true;
            current = current switch
            {
                Visual => VisualTreeHelper.GetParent(current),
                FrameworkContentElement content => content.Parent,
                _ => null
            };
        }

        return false;
    }

    private static IntPtr GetExtendedStyle(IntPtr handle) => IntPtr.Size == 8
        ? GetWindowLongPtr64(handle, GwlExStyle)
        : new IntPtr(GetWindowLong32(handle, GwlExStyle));

    private static void SetExtendedStyle(IntPtr handle, IntPtr style)
    {
        if (IntPtr.Size == 8)
            SetWindowLongPtr64(handle, GwlExStyle, style);
        else
            SetWindowLong32(handle, GwlExStyle, style.ToInt32());
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr handle, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr handle, int index, int value);
}
