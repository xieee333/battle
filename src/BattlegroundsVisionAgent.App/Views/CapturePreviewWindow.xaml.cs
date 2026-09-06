using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using BattlegroundsVisionAgent.Input;
using BattlegroundsVisionAgent.Vision.Capture;
using Microsoft.Win32;
using OpenCvSharp;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BattlegroundsVisionAgent.Vision.Geometry;
using BattlegroundsVisionAgent.Vision.Recognition;

namespace BattlegroundsVisionAgent.App.Views;

public partial class CapturePreviewWindow : System.Windows.Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private byte[]? _png;
    private readonly Dictionary<string, CalibrationRect> _regions = new();
    private readonly Dictionary<string, string> _regionLabels = new()
    {
        ["shop"] = "商店", ["hand"] = "手牌", ["board"] = "战场",
        ["gold"] = "金币", ["tier"] = "本数", ["discover"] = "发现（可选）"
    };
    private System.Windows.Point? _dragStart;
    private int _imageWidth;
    private int _imageHeight;

    public CapturePreviewWindow()
    {
        InitializeComponent();
        RegionChoice.ItemsSource = _regionLabels;
        RegionChoice.SelectedIndex = 0;
        Closed += (_, _) => _lifetime.Cancel();
    }

    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        CaptureButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        CalibrationControls.IsEnabled = false;
        BuildProfileButton.IsEnabled = false;
        OpenButton.IsEnabled = false;
        _png = null;
        Preview.Source = null;
        RegionCanvas.Children.Clear();
        try
        {
            for (var seconds = 3; seconds > 0; seconds--)
            {
                Status.Text = $"{seconds} 秒后截图，请切换到炉石窗口……";
                await Task.Delay(1000, _lifetime.Token);
            }
            var handle = WindowsGameWindowLocator.FindHearthstoneWindow();
            if (handle == IntPtr.Zero) throw new InvalidOperationException("未找到炉石客户端，请先打开游戏（官网网页不算）。");
            if (!new WindowsGameFocusProbe(handle).IsHearthstoneForeground())
                throw new InvalidOperationException("炉石不在前台，本次未截图。请重试并在倒计时期间切换到游戏。");
            using var source = new WindowsFrameSource(handle);
            using var frame = await source.CaptureAsync(_lifetime.Token);
            using var gray = new Mat();
            Cv2.CvtColor(frame.Image, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.MeanStdDev(gray, out var mean, out var deviation);
            if (deviation.Val0 < 2) throw new InvalidOperationException("截图接近纯色，可能是黑屏或游戏尚未渲染。请保持窗口可见后重试。");
            SetImage(frame.Image.ToBytes(".png"));
            Status.Text = $"截图完成：{frame.Image.Width} × {frame.Image.Height} · {frame.CapturedAt.ToLocalTime():HH:mm:ss}。请检查画面是否完整；尚未进行卡牌识别。";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Status.Text = $"截图失败：{exception.Message}"; }
        finally { CaptureButton.IsEnabled = true; OpenButton.IsEnabled = true; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_png is null) return;
        var dialog = new SaveFileDialog { Filter = "PNG 图片|*.png", FileName = $"hearthstone-{DateTime.Now:yyyyMMdd-HHmmss}.png" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllBytes(dialog.FileName, _png); Status.Text = $"已保存：{dialog.FileName}"; }
        catch (Exception exception) { Status.Text = $"保存失败：{exception.Message}"; }
    }

    private void SetImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        if (bitmap.PixelWidth > 16384 || bitmap.PixelHeight > 16384) throw new InvalidDataException("截图尺寸过大。");
        bitmap.Freeze();
        _png = bytes;
        _imageWidth = bitmap.PixelWidth;
        _imageHeight = bitmap.PixelHeight;
        ImageSurface.Width = _imageWidth;
        ImageSurface.Height = _imageHeight;
        Preview.Source = bitmap;
        _regions.Clear(); // A new frame must not silently inherit another scene's calibration.
        _dragStart = null;
        CalibrationControls.IsEnabled = true;
        BuildProfileButton.IsEnabled = true;
        SaveButton.IsEnabled = true;
        DrawRegions();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "PNG 截图|*.png", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 50 * 1024 * 1024) throw new InvalidDataException("截图文件不得超过 50 MB。");
            SetImage(File.ReadAllBytes(dialog.FileName));
            Status.Text = $"已导入 {_imageWidth} × {_imageHeight} 截图；请使用酒馆购物阶段画面进行校准。";
        }
        catch (Exception exception) { Status.Text = $"导入失败：{exception.Message}"; }
    }

    private void Region_Down(object sender, MouseButtonEventArgs e)
    {
        if (_png is null || !CalibrationControls.IsEnabled) return;
        _dragStart = e.GetPosition(RegionCanvas);
        RegionCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void Region_Move(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start) return;
        DrawRegions();
        try { DrawRect(CalibrationRect.FromDrag(start.X, start.Y, e.GetPosition(RegionCanvas).X, e.GetPosition(RegionCanvas).Y, _imageWidth, _imageHeight), "框选中", Brushes.Gold); }
        catch (InvalidDataException) { }
    }

    private void Region_Up(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is not { } start) return;
        _dragStart = null;
        RegionCanvas.ReleaseMouseCapture();
        try
        {
            if (RegionChoice.SelectedValue is not string key) throw new InvalidDataException("请先选择区域类型。");
            var end = e.GetPosition(RegionCanvas);
            _regions[key] = CalibrationRect.FromDrag(start.X, start.Y, end.X, end.Y, _imageWidth, _imageHeight);
            Status.Text = $"已标定{_regionLabels[key]}；仍需卡槽、模板与实机识别验证。";
        }
        catch (Exception exception) { Status.Text = exception.Message; }
        DrawRegions();
    }

    private void Region_Cancel(object sender, MouseEventArgs e)
    {
        _dragStart = null;
        DrawRegions();
    }

    private void DrawRegions()
    {
        RegionCanvas.Children.Clear();
        foreach (var pair in _regions) DrawRect(pair.Value, _regionLabels[pair.Key], Brushes.DeepSkyBlue);
        CalibrationStatus.Text = $"已标定 {_regions.Count} 个区域：{string.Join("、", _regions.Keys.Select(k => _regionLabels[k]))}。草稿不能直接用于自动运行。";
    }

    private void DrawRect(CalibrationRect rect, string label, Brush color)
    {
        var box = new System.Windows.Shapes.Rectangle
        {
            Width = rect.Width * _imageWidth, Height = rect.Height * _imageHeight,
            Stroke = color, StrokeThickness = 3, Fill = new SolidColorBrush(Color.FromArgb(28, 0, 160, 255)), IsHitTestVisible = false
        };
        Canvas.SetLeft(box, rect.X * _imageWidth);
        Canvas.SetTop(box, rect.Y * _imageHeight);
        RegionCanvas.Children.Add(box);
        var text = new TextBlock { Text = label, Foreground = Brushes.White, Background = Brushes.MidnightBlue, FontSize = 22, Padding = new Thickness(4), IsHitTestVisible = false };
        Canvas.SetLeft(text, rect.X * _imageWidth);
        Canvas.SetTop(text, rect.Y * _imageHeight);
        RegionCanvas.Children.Add(text);
    }

    private void RemoveRegion_Click(object sender, RoutedEventArgs e)
    {
        if (RegionChoice.SelectedValue is string key) _regions.Remove(key);
        DrawRegions();
    }

    private void SaveCalibration_Click(object sender, RoutedEventArgs e)
    {
        if (_regions.Count == 0) { Status.Text = "请先框选至少一个区域。"; return; }
        var dialog = new SaveFileDialog { Filter = "校准草稿|*.calibration.json", FileName = $"regions-{DateTime.Now:yyyyMMdd-HHmmss}.calibration.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, new RegionCalibration(1, _imageWidth, _imageHeight, new(_regions)).ToJson());
            Status.Text = $"草稿已保存：{dialog.FileName}。这不是 profile.json，尚不能启动识别。";
        }
        catch (Exception exception) { Status.Text = $"保存失败：{exception.Message}"; }
    }

    private void LoadCalibration_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "校准草稿|*.calibration.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 1024 * 1024) throw new InvalidDataException("校准文件过大。");
            var draft = RegionCalibration.Parse(File.ReadAllText(dialog.FileName), _imageWidth, _imageHeight);
            _regions.Clear();
            foreach (var pair in draft.Regions) _regions.Add(pair.Key, pair.Value);
            DrawRegions();
            Status.Text = "已加载草稿。尺寸匹配不代表场景匹配，请核对各区域是否对准当前画面。";
        }
        catch (Exception exception) { Status.Text = $"加载失败，原标定保留：{exception.Message}"; }
    }

    private void BuildProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_png is null || _imageWidth <= 0 || _imageHeight <= 0)
        {
            Status.Text = "请先截取或导入购物阶段截图。";
            return;
        }

        try
        {
            var calibration = new RegionCalibration(1, _imageWidth, _imageHeight, new(_regions));
            using var screenshot = Cv2.ImDecode(_png, ImreadModes.Color);
            var profileDirectory = Path.Combine(AppContext.BaseDirectory, "data", "vision");
            var result = VisionProfileBuilder.BuildShoppingProfile(screenshot, calibration, profileDirectory);
            Status.Text = $"购物阶段布局配置已生成：商店 {result.ShopSlotCount} 槽、手牌 {result.HandSlotCount} 槽、战场 {result.BoardSlotCount} 槽。" +
                          (result.HasGoldRegion && result.HasTierRegion
                              ? "金币和本数区域已保存；仍需数字模板后才能放行自动操作。"
                              : "金币/本数区域未完整标定，运行会保持安全暂停。") +
                          $" 配置位置：{result.ProfilePath}";
        }
        catch (Exception exception) { Status.Text = $"生成配置失败：{exception.Message}"; }
    }
}
