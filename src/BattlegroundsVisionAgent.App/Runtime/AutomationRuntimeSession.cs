using System.IO;
using BattlegroundsVisionAgent.Core.Configuration;
using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Core.Rules;
using BattlegroundsVisionAgent.Core.Runtime;
using BattlegroundsVisionAgent.Input;
using BattlegroundsVisionAgent.Vision.Capture;
using BattlegroundsVisionAgent.Vision.Recognition;

namespace BattlegroundsVisionAgent.App.Runtime;

public sealed class AutomationRuntimeSession : IAsyncDisposable
{
    private readonly string _catalogDatabasePath;
    private readonly string _profilePath;
    private readonly AutomationPlanner _planner;
    private readonly Func<AppSettings> _settingsProvider;
    private readonly RunState _runState;
    private readonly ActionVerifier _actionVerifier = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _runTask;
    private VisualSnapshotSource? _snapshotSource;
    private VisionRecognitionPipeline? _pipeline;
    private SafeInputExecutor? _inputExecutor;
    private WindowsInputBackend? _inputBackend;
    private bool _disposed;

    public AutomationRuntimeSession(
        string catalogDatabasePath,
        AutomationPlanner planner,
        Func<AppSettings> settingsProvider,
        RunState runState,
        string? profilePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogDatabasePath);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(settingsProvider);
        ArgumentNullException.ThrowIfNull(runState);
        _catalogDatabasePath = catalogDatabasePath;
        _profilePath = profilePath ?? Path.Combine(AppContext.BaseDirectory, "data", "vision", "profile.json");
        _planner = planner;
        _settingsProvider = settingsProvider;
        _runState = runState;
    }

    public event Action<AutomationTickResult>? TickCompleted;

    public async Task StartAsync(bool observationMode, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var existingTask = _runTask;
        if (existingTask is { IsCompleted: false })
            return;

        await StopAsync().ConfigureAwait(false);
        if (!File.Exists(_profilePath))
            throw new FileNotFoundException(
                "未找到视觉配置 profile.json。请先完成窗口校准并放入 data\\vision 目录。", _profilePath);
        if (!File.Exists(_catalogDatabasePath))
            throw new FileNotFoundException("未找到卡库数据库。请先导入或同步卡库。", _catalogDatabasePath);

        var windowHandle = WindowsGameWindowLocator.FindHearthstoneWindow();
        if (windowHandle == IntPtr.Zero)
            throw new InvalidOperationException("未找到炉石传说窗口，请先启动游戏客户端。");

        var pipeline = VisionRecognitionPipeline.Load(_profilePath, _catalogDatabasePath);
        var frameSource = new WindowsFrameSource(windowHandle);
        var snapshotSource = new VisualSnapshotSource(frameSource, pipeline.Recognizer);
        SafeInputExecutor? inputExecutor = null;
        WindowsInputBackend? inputBackend = null;
        try
        {
            if (!observationMode)
            {
                inputBackend = new WindowsInputBackend(windowHandle);
                inputExecutor = new SafeInputExecutor(
                    inputBackend,
                    _runState,
                    new WindowsGameFocusProbe(windowHandle),
                    new ActionBindingResolver());
            }

            var coordinator = new AutomationCoordinator(
                snapshotSource,
                _planner,
                _actionVerifier,
                _settingsProvider,
                _runState,
                observationMode ? null : new AutomationActionExecutorAdapter(inputExecutor!),
                options: new AutomationCoordinatorOptions
                {
                    ObservationMode = observationMode,
                    TickInterval = TimeSpan.FromMilliseconds(500)
                });
            coordinator.TickCompleted += OnTickCompleted;

            lock (_gate)
            {
                _pipeline = pipeline;
                _snapshotSource = snapshotSource;
                _inputExecutor = inputExecutor;
                _inputBackend = inputBackend;
                _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _runTask = coordinator.RunAsync(_cancellation.Token);
            }
        }
        catch
        {
            if (inputExecutor is not null)
                await inputExecutor.DisposeAsync().ConfigureAwait(false);
            inputBackend?.Dispose();
            snapshotSource.Dispose();
            pipeline.Dispose();
            throw;
        }
    }

    public async Task StopAsync()
    {
        Task? runTask;
        lock (_gate)
        {
            _cancellation?.Cancel();
            runTask = _runTask;
        }

        if (runTask is not null)
        {
            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await DisposeResourcesAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }

    private void OnTickCompleted(AutomationTickResult result) => TickCompleted?.Invoke(result);

    private async Task DisposeResourcesAsync()
    {
        SafeInputExecutor? inputExecutor;
        WindowsInputBackend? inputBackend;
        VisualSnapshotSource? snapshotSource;
        VisionRecognitionPipeline? pipeline;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            inputExecutor = _inputExecutor;
            inputBackend = _inputBackend;
            snapshotSource = _snapshotSource;
            pipeline = _pipeline;
            cancellation = _cancellation;
            _inputExecutor = null;
            _inputBackend = null;
            _snapshotSource = null;
            _pipeline = null;
            _cancellation = null;
            _runTask = null;
        }

        if (inputExecutor is not null)
            await inputExecutor.DisposeAsync().ConfigureAwait(false);
        inputBackend?.Dispose();
        snapshotSource?.Dispose();
        pipeline?.Dispose();
        cancellation?.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
