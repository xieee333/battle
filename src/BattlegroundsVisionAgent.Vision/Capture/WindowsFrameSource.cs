using System.ComponentModel;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Capture;

public sealed class WindowsFrameSource : IFrameSource
{
    private const uint DibRgbColors = 0;
    private const uint Srccopy = 0x00CC0020;
    private const int BgrBitCount = 32;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly IntPtr _windowHandle;
    private bool _disposed;

    public WindowsFrameSource(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
            throw new ArgumentException("A valid target window handle is required.", nameof(windowHandle));
        _windowHandle = windowHandle;
    }

    public IntPtr WindowHandle => _windowHandle;

    public async Task<CapturedFrame> CaptureAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(CaptureCore, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _captureGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _captureGate.Dispose();
    }

    private CapturedFrame CaptureCore()
    {
        if (!GetClientRect(_windowHandle, out var clientRect))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to query the target window client area.");

        var width = clientRect.Right - clientRect.Left;
        var height = clientRect.Bottom - clientRect.Top;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("The target window has no capturable client area.");

        if (!ClientToScreen(_windowHandle, out var clientOrigin))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to resolve the target client position on screen.");

        // Hearthstone renders through a hardware-accelerated surface. Its window DC can
        // contain an older backing image, while the screen DC contains the composited
        // frame the player actually sees. The caller verifies that the game is foreground
        // before using this source, so this captures the current visible client area.
        var sourceDc = GetDc(IntPtr.Zero);
        if (sourceDc == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to acquire the target window device context.");

        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previousObject = IntPtr.Zero;
        try
        {
            memoryDc = CreateCompatibleDC(sourceDc);
            if (memoryDc == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create a compatible device context.");

            var bitmapInfo = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = BgrBitCount,
                    Compression = 0
                }
            };
            bitmap = CreateDibSection(sourceDc, ref bitmapInfo, DibRgbColors, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to allocate a capture bitmap.");

            previousObject = SelectObject(memoryDc, bitmap);
            if (previousObject == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to select the capture bitmap.");
            if (!BitBlt(memoryDc, 0, 0, width, height, sourceDc, clientOrigin.X, clientOrigin.Y, Srccopy))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to copy the target window pixels.");

            using var bgra = Mat.FromPixelData(height, width, MatType.CV_8UC4, bits, width * 4).Clone();
            var bgr = new Mat();
            Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
            return new CapturedFrame(bgr, DateTimeOffset.UtcNow);
        }
        finally
        {
            if (previousObject != IntPtr.Zero && memoryDc != IntPtr.Zero)
                SelectObject(memoryDc, previousObject);
            if (bitmap != IntPtr.Zero)
                DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero)
                DeleteDc(memoryDc);
            ReleaseDc(IntPtr.Zero, sourceDc);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

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
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint ImageSize;
        public int XpelsPerMeter;
        public int YpelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [DllImport("user32.dll", EntryPoint = "GetDC", SetLastError = true)]
    private static extern IntPtr GetDc(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr windowHandle, out Point point);

    [DllImport("user32.dll", EntryPoint = "ReleaseDC", SetLastError = true)]
    private static extern int ReleaseDc(IntPtr windowHandle, IntPtr deviceContext);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr windowHandle, out Rect rect);

    [DllImport("gdi32.dll", EntryPoint = "CreateCompatibleDC", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", EntryPoint = "DeleteDC", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDc(IntPtr deviceContext);

    [DllImport("gdi32.dll", EntryPoint = "CreateDIBSection", SetLastError = true)]
    private static extern IntPtr CreateDibSection(IntPtr deviceContext, ref BitmapInfo bitmapInfo, uint usage,
        out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicsObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr graphicsObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr destinationDc, int x, int y, int width, int height,
        IntPtr sourceDc, int sourceX, int sourceY, uint rasterOperation);
}
