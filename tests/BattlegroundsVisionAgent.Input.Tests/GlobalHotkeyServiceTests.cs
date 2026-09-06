using BattlegroundsVisionAgent.Core.Runtime;
using BattlegroundsVisionAgent.Input;

namespace BattlegroundsVisionAgent.Input.Tests;

public sealed class GlobalHotkeyServiceTests
{
    private static readonly IntPtr TestWindowHandle = new(1);

    [Fact]
    public void Start_RejectsCollidingHotkeysWithoutRegisteringEither()
    {
        var registrar = new SpyHotkeyRegistrar();
        using var service = new GlobalHotkeyService(new RunState(), registrar);
        service.AttachWindow(TestWindowHandle);

        var error = Assert.Throws<ArgumentException>(() => service.Start("F7", "F7"));

        Assert.Contains("collide", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(registrar.Registered);
    }

    [Fact]
    public void Start_RejectsEquivalentHotkeysWithDifferentTextOrder()
    {
        var registrar = new SpyHotkeyRegistrar();
        using var service = new GlobalHotkeyService(new RunState(), registrar);
        service.AttachWindow(TestWindowHandle);

        Assert.Throws<ArgumentException>(() => service.Start("CTRL+F7", "F7+CTRL"));

        Assert.Empty(registrar.Registered);
    }

    [Fact]
    public void Start_RejectsInvalidHotkeyWithoutRegisteringEither()
    {
        var registrar = new SpyHotkeyRegistrar();
        using var service = new GlobalHotkeyService(new RunState(), registrar);
        service.AttachWindow(TestWindowHandle);

        Assert.Throws<ArgumentException>(() => service.Start("bad hotkey", "F8"));

        Assert.Empty(registrar.Registered);
    }

    [Fact]
    public void Dispatch_PausesThenContinuesAndEmergencyStops()
    {
        var state = new RunState();
        var registrar = new SpyHotkeyRegistrar();
        using var service = new GlobalHotkeyService(state, registrar);
        service.AttachWindow(TestWindowHandle);
        service.Start("F7", "F8");

        service.Dispatch(GlobalHotkeyService.PauseToggleId);
        Assert.Equal(RunStatus.Paused, state.Status);
        service.Dispatch(GlobalHotkeyService.PauseToggleId);
        Assert.Equal(RunStatus.Running, state.Status);
        service.Dispatch(GlobalHotkeyService.EmergencyStopId);

        Assert.Equal(RunStatus.EmergencyStopped, state.Status);
        Assert.Equal([GlobalHotkeyService.PauseToggleId, GlobalHotkeyService.EmergencyStopId], registrar.Registered.Select(pair => pair.Id));
    }

    [Fact]
    public void HandleWindowMessage_ForwardsRegisteredWmHotkeyToPauseAndEmergencyStop()
    {
        var state = new RunState();
        using var service = new GlobalHotkeyService(state, new SpyHotkeyRegistrar());
        service.AttachWindow(TestWindowHandle);
        service.Start("F7", "F8");

        var pauseHandled = service.HandleWindowMessage(
            GlobalHotkeyService.WmHotkey,
            new IntPtr(GlobalHotkeyService.PauseToggleId));
        Assert.True(pauseHandled);
        Assert.Equal(RunStatus.Paused, state.Status);

        var stopHandled = service.HandleWindowMessage(
            GlobalHotkeyService.WmHotkey,
            new IntPtr(GlobalHotkeyService.EmergencyStopId));

        Assert.Equal(RunStatus.EmergencyStopped, state.Status);
        Assert.True(stopHandled);
        Assert.False(service.HandleWindowMessage(0, new IntPtr(GlobalHotkeyService.PauseToggleId)));
    }

    [Fact]
    public void Dispose_UnregistersEverySuccessfullyRegisteredHotkey()
    {
        var registrar = new SpyHotkeyRegistrar();
        var service = new GlobalHotkeyService(new RunState(), registrar);
        service.AttachWindow(TestWindowHandle);
        service.Start("F7", "F8");

        service.Dispose();

        Assert.Equal(
            [(TestWindowHandle, GlobalHotkeyService.PauseToggleId), (TestWindowHandle, GlobalHotkeyService.EmergencyStopId)],
            registrar.Unregistered);
    }

    [Fact]
    public void Start_WhenSecondRegistrationFails_RollsBackUsingAttachedWindowHandle()
    {
        var registrar = new RejectEmergencyStopRegistrar();
        using var service = new GlobalHotkeyService(new RunState(), registrar);
        service.AttachWindow(TestWindowHandle);

        Assert.Throws<InvalidOperationException>(() => service.Start("F7", "F8"));

        Assert.Equal([(TestWindowHandle, GlobalHotkeyService.PauseToggleId)], registrar.Unregistered);
    }

    private sealed class SpyHotkeyRegistrar : IHotkeyRegistrar
    {
        public List<(IntPtr WindowHandle, int Id, KeyShortcut Shortcut)> Registered { get; } = [];
        public List<(IntPtr WindowHandle, int Id)> Unregistered { get; } = [];

        public bool Register(IntPtr windowHandle, int id, KeyShortcut shortcut)
        {
            Registered.Add((windowHandle, id, shortcut));
            return true;
        }

        public void Unregister(IntPtr windowHandle, int id) => Unregistered.Add((windowHandle, id));
    }

    private sealed class RejectEmergencyStopRegistrar : IHotkeyRegistrar
    {
        public List<(IntPtr WindowHandle, int Id)> Unregistered { get; } = [];

        public bool Register(IntPtr windowHandle, int id, KeyShortcut shortcut) =>
            id != GlobalHotkeyService.EmergencyStopId;

        public void Unregister(IntPtr windowHandle, int id) => Unregistered.Add((windowHandle, id));
    }
}
