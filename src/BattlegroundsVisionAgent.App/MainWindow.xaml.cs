using System.IO;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using BattlegroundsVisionAgent.App.ViewModels;
using BattlegroundsVisionAgent.App.Views;
using BattlegroundsVisionAgent.Core.Configuration;
using BattlegroundsVisionAgent.Core.Runtime;
using BattlegroundsVisionAgent.Input;
using BattlegroundsVisionAgent.Vision.Catalog;

namespace BattlegroundsVisionAgent.App;

public partial class MainWindow : Window
{
    private readonly StatusOverlayWindow _statusOverlay;
    private readonly RunState _runState = new(RunStatus.Paused);
    private readonly SettingsRepository _settingsRepository;
    private readonly BlizzardCatalogSyncService _officialCatalogSync;
    private HwndSource? _windowSource;
    private GlobalHotkeyService? _globalHotkeys;

    public MainWindow()
    {
        InitializeComponent();
        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BattlegroundsVisionAgent",
            "settings.json");
        _settingsRepository = new SettingsRepository(settingsPath);
        _officialCatalogSync = new BlizzardCatalogSyncService();
        DataContext = new MainViewModel(runState: _runState, officialCatalogSync: _officialCatalogSync);
        _statusOverlay = new StatusOverlayWindow { DataContext = DataContext };
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var viewModel = (MainViewModel)DataContext;
        try
        {
            viewModel.ApplySettings(await _settingsRepository.LoadAsync(CancellationToken.None));
        }
        catch (Exception exception)
        {
            viewModel.RunStatus = $"设置加载失败：{exception.Message}";
        }

        _statusOverlay.Show();
        if (_windowSource is not null)
        {
            return;
        }

        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        if (_windowSource is null)
        {
            return;
        }

        _windowSource.AddHook(HandleWindowMessage);
        try
        {
            var settings = viewModel.Settings;
            _globalHotkeys = new GlobalHotkeyService(_runState);
            _globalHotkeys.AttachWindow(_windowSource.Handle);
            _globalHotkeys.Start(settings.PauseHotkey, settings.EmergencyStopHotkey);
        }
        catch (InvalidOperationException)
        {
            _windowSource.RemoveHook(HandleWindowMessage);
            _globalHotkeys?.Dispose();
            _globalHotkeys = null;
            _windowSource = null;
            ((MainViewModel)DataContext).RunStatus = "全局快捷键注册失败";
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(HandleWindowMessage);
        }

        _globalHotkeys?.Dispose();
        _officialCatalogSync.Dispose();
        var viewModel = (MainViewModel)DataContext;
        try
        {
            await viewModel.DisposeRuntimeAsync();
        }
        catch
        {
            // Closing must not be blocked by runtime cleanup failures.
        }
        try
        {
            await _settingsRepository.SaveAsync(viewModel.Settings, CancellationToken.None);
        }
        catch
        {
            // Closing must not be blocked by a settings write failure.
        }
        _statusOverlay.Close();
    }

    private async void UpdateCatalog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择卡库更新包",
            Filter = "卡库更新包 (*.zip)|*.zip",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            await ((MainViewModel)DataContext).UpdateCatalogAsync(dialog.FileName);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "卡库更新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void CapturePreview_Click(object sender, RoutedEventArgs e)
    {
        _runState.Pause();
        try
        {
            await ((MainViewModel)DataContext).StopRuntimeAsync();
            new CapturePreviewWindow { Owner = this }.ShowDialog();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "截图预览未启动", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void RecognitionValidation_Click(object sender, RoutedEventArgs e)
    {
        _runState.Pause();
        try
        {
            await ((MainViewModel)DataContext).StopRuntimeAsync();
            new RecognitionValidationWindow { Owner = this }.ShowDialog();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "识别校验未启动", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void SyncOfficialCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || !button.IsEnabled) return;
        button.IsEnabled = false;
        try
        {
            await ((MainViewModel)DataContext).SyncOfficialCatalogAsync(
                OfficialCatalogSyncOptions.FromEnvironment());
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "官网卡库同步失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { button.IsEnabled = true; }
    }

    private IntPtr HandleWindowMessage(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (_globalHotkeys?.HandleWindowMessage(message, wParam) == true)
        {
            handled = true;
        }

        return IntPtr.Zero;
    }
}
