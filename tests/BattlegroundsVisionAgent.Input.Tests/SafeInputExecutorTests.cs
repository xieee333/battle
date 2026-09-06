using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Core.Runtime;
using BattlegroundsVisionAgent.Input;

namespace BattlegroundsVisionAgent.Input.Tests;

public sealed class SafeInputExecutorTests
{
    [Fact]
    public async Task Execute_DoesNotSendInput_WhenEmergencyStopped()
    {
        var backend = new SpyInputBackend();
        var state = new RunState();
        state.EmergencyStop();
        await using var executor = CreateExecutor(backend, state);

        var result = await executor.ExecuteAsync(new RefreshAction(7), Snapshot(), CancellationToken.None);

        Assert.False(result.Sent);
        Assert.Equal(InputExecutionReason.EmergencyStopped, result.Reason);
        Assert.Empty(backend.SentBindings);
    }

    [Fact]
    public async Task Execute_DoesNotSendInput_WhenPaused()
    {
        var backend = new SpyInputBackend();
        var state = new RunState();
        state.Pause();
        await using var executor = CreateExecutor(backend, state);

        var result = await executor.ExecuteAsync(new RefreshAction(7), Snapshot(), CancellationToken.None);

        Assert.False(result.Sent);
        Assert.Equal(InputExecutionReason.Paused, result.Reason);
        Assert.Empty(backend.SentBindings);
    }

    [Fact]
    public async Task EmergencyHotkey_ReleasesHeldInputAndBlocksSubsequentExecution()
    {
        var backend = new SpyInputBackend();
        var state = new RunState();
        await using var executor = CreateExecutor(backend, state);
        using var hotkeys = new GlobalHotkeyService(state, new AcceptingHotkeyRegistrar());
        hotkeys.AttachWindow(new IntPtr(1));
        hotkeys.Start("F7", "F8");

        hotkeys.Dispatch(GlobalHotkeyService.EmergencyStopId);
        var result = await executor.ExecuteAsync(new RefreshAction(7), Snapshot(), CancellationToken.None);

        Assert.Equal(1, backend.ReleaseCount);
        Assert.False(result.Sent);
        Assert.Equal(InputExecutionReason.EmergencyStopped, result.Reason);
        Assert.Empty(backend.SentBindings);
    }

    [Fact]
    public async Task Execute_DoesNotSendInput_WhenCancellationWasRequested()
    {
        var backend = new SpyInputBackend();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var executor = CreateExecutor(backend);

        var result = await executor.ExecuteAsync(new RefreshAction(7), Snapshot(), cancellation.Token);

        Assert.False(result.Sent);
        Assert.Equal(InputExecutionReason.Cancelled, result.Reason);
        Assert.Empty(backend.SentBindings);
    }

    [Fact]
    public async Task Execute_DoesNotSendInput_WhenGameIsNotForeground()
    {
        var backend = new SpyInputBackend();
        await using var executor = CreateExecutor(backend, focusProbe: new FixedFocusProbe(false));

        var result = await executor.ExecuteAsync(new RefreshAction(7), Snapshot(), CancellationToken.None);

        Assert.False(result.Sent);
        Assert.Equal(InputExecutionReason.GameNotForeground, result.Reason);
        Assert.Empty(backend.SentBindings);
    }

    [Fact]
    public async Task Execute_DoesNotSendInput_WhenSceneIsNotEligibleForAction()
    {
        var backend = new SpyInputBackend();
        await using var executor = CreateExecutor(backend);

        var result = await executor.ExecuteAsync(new RefreshAction(7), Snapshot(GamePhase.Combat), CancellationToken.None);

        Assert.False(result.Sent);
        Assert.Equal(InputExecutionReason.WrongGamePhase, result.Reason);
        Assert.Empty(backend.SentBindings);
    }

    [Fact]
    public async Task Execute_DoesNotSendInput_WhenSnapshotHasUnknownBlockingUi()
    {
        var backend = new SpyInputBackend();
        await using var executor = CreateExecutor(backend);
        var blockedSnapshot = Snapshot(hasUnknownBlockingUi: true);

        var result = await executor.ExecuteAsync(new RefreshAction(7), blockedSnapshot, CancellationToken.None);

        Assert.False(result.Sent);
        Assert.Equal(InputExecutionReason.SnapshotNotActionable, result.Reason);
        Assert.Empty(backend.SentBindings);
    }

    [Fact]
    public async Task Execute_DoesNotSendInput_WhenLayoutVersionChanged()
    {
        var backend = new SpyInputBackend();
        await using var executor = CreateExecutor(backend);

        var result = await executor.ExecuteAsync(new RefreshAction(7), Snapshot(layoutVersion: 8), CancellationToken.None);

        Assert.False(result.Sent);
        Assert.Equal(InputExecutionReason.LayoutVersionMismatch, result.Reason);
        Assert.Empty(backend.SentBindings);
    }

    [Fact]
    public async Task Execute_SellShortcutFailure_RequiresUserConfirmationWithoutMouseFallback()
    {
        var backend = new SpyInputBackend { KeyboardResult = InputBackendResult.Failed("key-failed") };
        var resolver = new ActionBindingResolver(new Dictionary<InputActionKind, string>
        {
            [InputActionKind.Sell] = "CTRL+S"
        });
        await using var executor = CreateExecutor(backend, resolver: resolver);

        var result = await executor.ExecuteAsync(new SellAction(7, "CARD_A", 0), Snapshot(), CancellationToken.None);

        Assert.False(result.Sent);
        Assert.True(result.RequiresUserConfirmation);
        Assert.Equal(InputExecutionReason.RequiresUserConfirmation, result.Reason);
        Assert.Single(backend.SentBindings);
        Assert.IsType<KeyboardBinding>(backend.SentBindings[0]);
    }

    [Fact]
    public async Task Execute_RefreshShortcutFailure_FallsBackToMouseOnceWhenEnabled()
    {
        var backend = new SpyInputBackend { KeyboardResult = InputBackendResult.Failed("key-failed") };
        var resolver = new ActionBindingResolver(new Dictionary<InputActionKind, string>
        {
            [InputActionKind.Refresh] = "F5"
        }, allowRefreshMouseFallback: true);
        await using var executor = CreateExecutor(backend, resolver: resolver);

        var result = await executor.ExecuteAsync(new RefreshAction(7), Snapshot(), CancellationToken.None);

        Assert.True(result.Sent);
        Assert.Equal(InputExecutionReason.SentWithMouseFallback, result.Reason);
        Assert.Collection(backend.SentBindings,
            binding => Assert.IsType<KeyboardBinding>(binding),
            binding => Assert.IsType<MouseBinding>(binding));
    }

    [Fact]
    public async Task Execute_BuyShortcutFailure_DoesNotFallBackToMouse()
    {
        var backend = new SpyInputBackend { KeyboardResult = InputBackendResult.Failed("key-failed") };
        var resolver = new ActionBindingResolver(new Dictionary<InputActionKind, string>
        {
            [InputActionKind.Buy] = "F6"
        }, allowRefreshMouseFallback: true);
        await using var executor = CreateExecutor(backend, resolver: resolver);

        var result = await executor.ExecuteAsync(new BuyAction(7, "CARD_A", 0), Snapshot(), CancellationToken.None);

        Assert.False(result.Sent);
        Assert.Equal(InputExecutionReason.BackendFailed, result.Reason);
        Assert.Single(backend.SentBindings);
        Assert.IsType<KeyboardBinding>(backend.SentBindings[0]);
    }

    private static SafeInputExecutor CreateExecutor(
        SpyInputBackend backend,
        RunState? state = null,
        IGameFocusProbe? focusProbe = null,
        ActionBindingResolver? resolver = null) =>
        new(backend, state ?? new RunState(), focusProbe ?? new FixedFocusProbe(true), resolver ?? new ActionBindingResolver());

    private static GameSnapshot Snapshot(
        GamePhase phase = GamePhase.Shopping,
        long layoutVersion = 7,
        bool hasUnknownBlockingUi = false) => new(
        layoutVersion,
        0.99,
        DateTimeOffset.UnixEpoch,
        phase,
        10,
        2,
        [Card("CARD_A", CardZone.Shop, 0)],
        [Card("CARD_A", CardZone.Hand, 0)],
        [Card("CARD_A", CardZone.Board, 0)],
        [],
        10,
        7,
        false,
        hasUnknownBlockingUi);

    private static CardObservation Card(string cardId, CardZone zone, int slot) =>
        new(cardId, zone, slot, false, new NormalizedRect(0.1, 0.1, 0.1, 0.1), 0.99);

    private sealed class FixedFocusProbe(bool isForeground) : IGameFocusProbe
    {
        public bool IsHearthstoneForeground() => isForeground;
    }

    private sealed class SpyInputBackend : IWindowsInputBackend
    {
        public List<InputBinding> SentBindings { get; } = [];
        public int ReleaseCount { get; private set; }
        public InputBackendResult KeyboardResult { get; init; } = InputBackendResult.Ok();
        public InputBackendResult MouseResult { get; init; } = InputBackendResult.Ok();

        public Task<InputBackendResult> SendAsync(InputBinding binding, CancellationToken cancellationToken)
        {
            SentBindings.Add(binding);
            return Task.FromResult(binding is KeyboardBinding ? KeyboardResult : MouseResult);
        }

        public Task ReleaseAllAsync(CancellationToken cancellationToken)
        {
            ReleaseCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class AcceptingHotkeyRegistrar : IHotkeyRegistrar
    {
        public bool Register(IntPtr windowHandle, int id, KeyShortcut shortcut) => true;

        public void Unregister(IntPtr windowHandle, int id)
        {
        }
    }
}
