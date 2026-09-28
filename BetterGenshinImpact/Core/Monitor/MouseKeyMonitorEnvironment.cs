using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recorder;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.Model;
using BetterGenshinImpact.Platform.Wine;
using Gma.System.MouseKeyHook;
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Vanara.PInvoke;

namespace BetterGenshinImpact.Core.Monitor;

// 将系统钩子、配置、录制及输入边界集中到此处；离线测试无需初始化 App/TaskContext。
internal sealed class MouseKeyMonitorEnvironment
{
    internal Func<IKeyboardMouseEvents> CreateEventSource { get; init; } = () => MouseKeyMonitor.GlobalHook;
    internal Action<IKeyboardMouseEvents> ReleaseEventSource { get; init; } = MouseKeyMonitor.ReleaseGlobalHook;
    internal Func<bool> IsInputDisabled { get; init; } = () => TaskContext.Instance().Config.DisableInputMonitor;
    internal Func<bool> IsWine { get; init; } = () => WinePlatformAddon.IsRunningOnWine;
    internal Func<MouseKeyMonitorSettings> GetSettings { get; init; } = () =>
    {
        var config = TaskContext.Instance().Config;
        return new MouseKeyMonitorSettings(
            config.KeyBindingsConfig.PickUpOrInteract.ToWinFormKeys(),
            config.KeyBindingsConfig.PickUpOrInteract.ToVK(),
            config.KeyBindingsConfig.Jump.ToWinFormKeys(),
            config.KeyBindingsConfig.Jump.ToVK(),
            config.MacroConfig.FFireInterval, config.MacroConfig.SpaceFireInterval);
    };
    internal Func<bool> FRepeatEnabled { get; init; } = () => TaskContext.Instance().Config.MacroConfig.FPressHoldToContinuationEnabled;
    internal Func<bool> SpaceRepeatEnabled { get; init; } = () => TaskContext.Instance().Config.MacroConfig.SpacePressHoldToContinuationEnabled;
    internal Func<bool> IsGameActive { get; init; } = () => SystemControl.IsGenshinImpactActive();
    internal Func<DateTime> Now { get; init; } = () => DateTime.Now;
    internal Action<Keys> PrimeChat { get; init; } = ChatUiHotkeyGuard.PrimeFromChatKey;
    internal Action<KeyEventArgs, bool> RecordKey { get; init; } = (e, down) =>
    {
        if (down) GlobalKeyMouseRecord.Instance.GlobalHookKeyDown(e, DateTime.UtcNow);
        else GlobalKeyMouseRecord.Instance.GlobalHookKeyUp(e, DateTime.UtcNow);
    };
    internal Action<MouseEventExtArgs, MouseMonitorEvent> RecordMouse { get; init; } = (e, kind) =>
    {
        var record = GlobalKeyMouseRecord.Instance;
        switch (kind)
        {
            case MouseMonitorEvent.Down: record.GlobalHookMouseDown(e, DateTime.UtcNow); break;
            case MouseMonitorEvent.Up: record.GlobalHookMouseUp(e, DateTime.UtcNow); break;
            case MouseMonitorEvent.Move: record.GlobalHookMouseMoveTo(e, DateTime.UtcNow); break;
            case MouseMonitorEvent.Wheel: record.GlobalHookMouseWheel(e, DateTime.UtcNow); break;
        }
    };
    internal Action<nint, User32.VK> SendRepeat { get; init; } = (handle, key) => Simulation.PostMessage(handle).KeyPress(key);
    internal Func<double, Action, IHotkeyRepeatTimer> CreateRepeatTimer { get; init; } = (interval, callback) => new HotkeyRepeatTimer(interval, callback);
    internal IDictionary<Keys, KeyboardHook> KeyboardHooks { get; init; } = KeyboardHook.AllKeyboardHooks;
    internal IDictionary<MouseButtons, MouseHook> MouseHooks { get; init; } = MouseHook.AllMouseHooks;
}

internal sealed record MouseKeyMonitorSettings(Keys PickUpKey, User32.VK PickUpKeyCode,
    Keys JumpKey, User32.VK JumpKeyCode, double FInterval, double SpaceInterval);

internal enum MouseMonitorEvent { Down, Up, Move, Wheel }

internal interface IHotkeyRepeatTimer : IDisposable
{
    bool Enabled { get; }
    void Start();
    void Stop();
}

internal sealed class HotkeyRepeatTimer : IHotkeyRepeatTimer
{
    private readonly System.Timers.Timer _timer;

    internal HotkeyRepeatTimer(double interval, Action callback)
    {
        _timer = new System.Timers.Timer(interval);
        _timer.Elapsed += (_, _) => callback();
    }

    public bool Enabled => _timer.Enabled;
    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
    public void Dispose() => _timer.Dispose();
}
