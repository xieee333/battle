using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace BattlegroundsVisionAgent.App.Views;

public enum SellConfirmationResult
{
    ConfirmSell,
    KeepStill
}

public sealed class SellConfirmationState
{
    private bool _isFinalized;

    public SellConfirmationResult Result { get; private set; } = SellConfirmationResult.KeepStill;

    public void ConfirmSell()
    {
        if (_isFinalized)
            return;

        Result = SellConfirmationResult.ConfirmSell;
        _isFinalized = true;
    }

    public void KeepStill()
    {
        if (_isFinalized)
            return;

        Result = SellConfirmationResult.KeepStill;
        _isFinalized = true;
    }
}

public partial class SellConfirmationWindow : Window
{
    private readonly DispatcherTimer _timeoutTimer;
    private readonly SellConfirmationState _state = new();

    public SellConfirmationWindow(TimeSpan? timeout = null)
    {
        InitializeComponent();
        _timeoutTimer = new DispatcherTimer { Interval = timeout ?? TimeSpan.FromSeconds(5) };
        _timeoutTimer.Tick += OnTimeout;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public SellConfirmationResult Result => _state.Result;

    private void OnLoaded(object sender, RoutedEventArgs e) => _timeoutTimer.Start();

    private void OnClosed(object? sender, EventArgs e) => _timeoutTimer.Stop();

    private void OnTimeout(object? sender, EventArgs e)
    {
        _state.KeepStill();
        Close();
    }

    private void OnConfirmSell(object sender, RoutedEventArgs e)
    {
        _state.ConfirmSell();
        Close();
    }

    private void OnKeepStill(object sender, RoutedEventArgs e)
    {
        _state.KeepStill();
        Close();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        _state.KeepStill();
        e.Handled = true;
        Close();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        _state.KeepStill();
        Close();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e) =>
        _timeoutTimer.Stop();
}
