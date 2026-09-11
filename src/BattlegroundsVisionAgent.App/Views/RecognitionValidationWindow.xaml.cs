using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BattlegroundsVisionAgent.App.ViewModels;
using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Catalog;
using BattlegroundsVisionAgent.Vision.Recognition;
using BattlegroundsVisionAgent.Vision.Validation;
using Microsoft.Win32;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.App.Views;

public partial class RecognitionValidationWindow : System.Windows.Window
{
    private readonly RecognitionValidationViewModel _viewModel = new();
    private readonly RecognitionValidationSampleRepository _sampleRepository = new();
    private byte[]? _png;
    private string _imagePath = "当前内存截图";
    private int _imageWidth;
    private int _imageHeight;
    private CardCatalogSnapshot? _catalogSnapshot;

    public RecognitionValidationWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    public void LoadImage(byte[] png, string imagePath = "当前内存截图")
    {
        SetImage(png, imagePath);
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择需要校验的截图",
            Filter = "PNG 截图|*.png|所有图片|*.png;*.jpg;*.jpeg",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            if (new FileInfo(dialog.FileName).Length > 50 * 1024 * 1024)
                throw new InvalidDataException("截图文件不得超过 50 MB。");
            SetImage(File.ReadAllBytes(dialog.FileName), dialog.FileName);
        }
        catch (Exception exception)
        {
            _viewModel.SetStatus($"导入失败：{exception.Message}");
        }
    }

    private void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (_png is null)
        {
            SetStatus("请先导入或传入截图。");
            return;
        }

        var profilePath = Path.Combine(AppContext.BaseDirectory, "data", "vision", "profile.json");
        var catalogPath = Path.Combine(AppContext.BaseDirectory, "data", "catalog", "catalog.db");
        if (!File.Exists(profilePath))
        {
            SetStatus("尚未找到 profile.json。请先在‘截图预览’中框选商店、手牌、战场并生成购物阶段配置。");
            return;
        }

        if (!File.Exists(catalogPath))
        {
            SetStatus("尚未找到本地卡库 catalog.db；请先点击主窗口的‘更新卡库’或‘同步国服卡库’。");
            return;
        }

        try
        {
            using var screenshot = Cv2.ImDecode(_png, ImreadModes.Color);
            if (screenshot.Empty())
                throw new InvalidDataException("图片无法解码。");
            _catalogSnapshot = new CardCatalog(catalogPath).ReadSnapshot();
            using var pipeline = VisionRecognitionPipeline.Load(profilePath, catalogPath);
            var result = pipeline.Recognizer.Recognize(screenshot, DateTimeOffset.Now);
            var names = _catalogSnapshot.Entries
                .GroupBy(entry => entry.CardId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().NameZhCn, StringComparer.OrdinalIgnoreCase);
            var compatibility = _sampleRepository.ValidateAgainstCatalog(_catalogSnapshot);
            _viewModel.LoadResult(result, names, _imagePath);
            _viewModel.SetCatalogStatus(
                $"当前卡库：{_catalogSnapshot.Metadata?.Version ?? "未标注版本"} · {_catalogSnapshot.Entries.Count} 张",
                compatibility.ToDisplayText());
            DrawOverlay(result);
            AnalyzeButton.IsEnabled = true;
            RefreshRecommendationsButton.IsEnabled = true;
            SaveFeedbackButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            SetStatus($"离线识别失败：{exception.Message}");
        }
    }

    private void RefreshRecommendations_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.RebuildRecommendations();
        SetStatus("已根据你当前标记的识别状态、位置和期望结果刷新精进建议。");
    }

    private void SaveFeedback_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_catalogSnapshot is null || _png is null)
                throw new InvalidOperationException("请先完成识别，再保存校验反馈。");
            var feedback = _viewModel.CreateFeedback(_catalogSnapshot.Metadata?.Version ?? "未标注版本");
            var result = _sampleRepository.Save(feedback, _png, _catalogSnapshot);
            SetStatus(result.ToDisplayText() + $" 路径：{result.FeedbackPath}");
            _viewModel.SetCatalogStatus(
                $"当前卡库：{_catalogSnapshot.Metadata?.Version ?? "未标注版本"} · {_catalogSnapshot.Entries.Count} 张",
                result.Compatibility.ToDisplayText());
        }
        catch (Exception exception)
        {
            SetStatus($"保存反馈失败：{exception.Message}");
        }
    }

    private void SetImage(byte[] bytes, string imagePath)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        if (bitmap.PixelWidth > 16384 || bitmap.PixelHeight > 16384)
            throw new InvalidDataException("截图尺寸过大。");
        bitmap.Freeze();

        _png = bytes;
        _imagePath = string.IsNullOrWhiteSpace(imagePath) ? "当前内存截图" : imagePath;
        _imageWidth = bitmap.PixelWidth;
        _imageHeight = bitmap.PixelHeight;
        ImageSurface.Width = _imageWidth;
        ImageSurface.Height = _imageHeight;
        Preview.Source = bitmap;
        EmptyImageText.Visibility = Visibility.Collapsed;
        OverlayCanvas.Children.Clear();
        AnalyzeButton.IsEnabled = true;
        RefreshRecommendationsButton.IsEnabled = false;
        SaveFeedbackButton.IsEnabled = false;
        _viewModel.SetStatus($"已导入 {_imageWidth} × {_imageHeight} 截图，点击‘开始识别’。");
    }

    private void DrawOverlay(SnapshotRecognitionResult result)
    {
        OverlayCanvas.Children.Clear();
        foreach (var card in result.Cards)
        {
            var bounds = card.Bounds;
            var color = card.Observation.CardId == "UNKNOWN"
                ? Brushes.IndianRed
                : card.Confidence < 0.80 ? Brushes.Orange : Brushes.LawnGreen;
            var box = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(2, bounds.Width * _imageWidth),
                Height = Math.Max(2, bounds.Height * _imageHeight),
                Stroke = color,
                StrokeThickness = 3,
                Fill = new SolidColorBrush(Color.FromArgb(24, ((SolidColorBrush)color).Color.R,
                    ((SolidColorBrush)color).Color.G, ((SolidColorBrush)color).Color.B))
            };
            Canvas.SetLeft(box, bounds.X * _imageWidth);
            Canvas.SetTop(box, bounds.Y * _imageHeight);
            OverlayCanvas.Children.Add(box);

            var label = new TextBlock
            {
                Text = $"{ZoneLabel(card.Observation.CardZone)} {card.SlotIndex + 1} · {card.Observation.CardId} · {card.Confidence:P0}",
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(220, 8, 13, 24)),
                Padding = new Thickness(4, 2, 4, 2),
                FontSize = 13
            };
            Canvas.SetLeft(label, bounds.X * _imageWidth);
            Canvas.SetTop(label, Math.Max(0, bounds.Y * _imageHeight - 24));
            OverlayCanvas.Children.Add(label);
        }
    }

    private void SetStatus(string text)
    {
        _viewModel.SetStatus(text);
    }

    private static string ZoneLabel(CardZone zone) => zone switch
    {
        CardZone.Shop => "商店",
        CardZone.Hand => "手牌",
        CardZone.Board => "战场",
        CardZone.Discover => "发现",
        _ => zone.ToString()
    };
}
