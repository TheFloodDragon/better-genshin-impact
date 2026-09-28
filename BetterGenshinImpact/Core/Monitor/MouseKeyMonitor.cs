using BetterGenshinImpact.Model;
using Gma.System.MouseKeyHook;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Vanara.PInvoke;

namespace BetterGenshinImpact.Core.Monitor;

public partial class MouseKeyMonitor : IDisposable
{
    private enum ListeningMode { None, Local, CloudHotkeys }

    private readonly object _stateLock = new();
    private readonly MouseKeyMonitorEnvironment _environment;
    private ListeningMode _mode;
    private long _session;
    private bool _disposed;
    private EventSubscription? _subscription;
    private IHotkeyRepeatTimer? _fTimer;
    private IHotkeyRepeatTimer? _spaceTimer;
    private Keys _pickUpKey = Keys.F;
    private User32.VK _pickUpKeyCode = User32.VK.VK_F;
    private Keys _releaseControlKey = Keys.Space;
    private User32.VK _releaseControlKeyCode = User32.VK.VK_SPACE;
    private DateTime _firstFKeyDownTime = DateTime.MaxValue;
    private DateTime _firstSpaceKeyDownTime = DateTime.MaxValue;
    private nint _hWnd;

    private static IKeyboardMouseEvents? _globalHook;
    private static readonly object GlobalHookLock = new();

    public static IKeyboardMouseEvents GlobalHook
    {
        get
        {
            lock (GlobalHookLock)
                return _globalHook ??= Hook.GlobalEvents();
        }
    }

    internal static void ReleaseGlobalHook(IKeyboardMouseEvents hook)
    {
        lock (GlobalHookLock)
        {
            if (ReferenceEquals(_globalHook, hook)) _globalHook = null;
            hook.Dispose();
        }
    }

    public MouseKeyMonitor() : this(new MouseKeyMonitorEnvironment()) { }

    internal MouseKeyMonitor(MouseKeyMonitorEnvironment environment) => _environment = environment;

    public void Subscribe(nint gameHandle) => SubscribeCore(gameHandle, ListeningMode.Local);

    /// <summary>
    /// 仅监听云模式允许的安全快捷键，不启动录制、聊天预判或游戏输入连发。
    /// 与 Subscribe 一样在 UI 线程调用，且仍尊重 DisableInputMonitor。
    /// </summary>
    public void SubscribeCloudHotkeys(nint gameHandle) => SubscribeCore(gameHandle, ListeningMode.CloudHotkeys);

    private void SubscribeCore(nint gameHandle, ListeningMode mode)
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_environment.IsInputDisabled())
            {
                StopListening();
                return;
            }

            if (_mode == mode && _hWnd == gameHandle) return;
            StopListening();
            _hWnd = gameHandle;
            var session = _session;
            try
            {
                if (mode == ListeningMode.Local)
                {
                    var settings = _environment.GetSettings();
                    _pickUpKey = settings.PickUpKey;
                    _pickUpKeyCode = settings.PickUpKeyCode;
                    _releaseControlKey = settings.JumpKey;
                    _releaseControlKeyCode = settings.JumpKeyCode;
                    IHotkeyRepeatTimer? fTimer = null;
                    fTimer = _environment.CreateRepeatTimer(settings.FInterval,
                        () => OnRepeatTimerElapsed(session, fTimer, _pickUpKeyCode));
                    _fTimer = fTimer;
                    IHotkeyRepeatTimer? spaceTimer = null;
                    spaceTimer = _environment.CreateRepeatTimer(settings.SpaceInterval,
                        () => OnRepeatTimerElapsed(session, spaceTimer, _releaseControlKeyCode));
                    _spaceTimer = spaceTimer;
                }

                if (!_environment.IsWine())
                {
                    _subscription = new EventSubscription(this, _environment.CreateEventSource(), session);
                    _subscription.Attach();
                }
                else if (mode == ListeningMode.Local)
                {
                    // 保留 Wine 本地轮询路径；云模式绝不启动连发轮询。
                    TrySubscribeWinePolling(session);
                }

                _mode = mode;
            }
            catch (Exception startError)
            {
                try { StopListening(); }
                catch (Exception cleanupError) { throw new AggregateException(startError, cleanupError); }
                throw;
            }
        }
    }

    private bool IsCurrent(long session) => !_disposed && _mode != ListeningMode.None && _session == session;

    private bool CanDispatch(long session)
    {
        lock (_stateLock) return IsCurrent(session) && !_environment.IsInputDisabled();
    }

    private void OnRepeatTimerElapsed(long session, IHotkeyRepeatTimer? timer, User32.VK key)
    {
        // Stop/Dispose 不保证已排队的 Elapsed 消失。会话校验排除旧回调，
        // 同一锁保证切换返回前已在途的短输入完成，之后不会再发送输入。
        lock (_stateLock)
        {
            if (!IsCurrent(session) || _mode != ListeningMode.Local || timer?.Enabled != true
                || _environment.IsInputDisabled()) return;
            _environment.SendRepeat(_hWnd, key);
        }
    }

    private void HandleKey(long session, object? sender, KeyEventArgs e, bool down)
    {
        KeyboardHook? hook;
        bool cloudOnly;
        lock (_stateLock)
        {
            if (!IsCurrent(session) || _environment.IsInputDisabled()) return;
            cloudOnly = _mode == ListeningMode.CloudHotkeys;
            // 必须先决定模式，云事件不能接触任何录制或聊天识别入口。
            if (!cloudOnly)
            {
                _environment.RecordKey(e, down);
                if (down && _environment.IsGameActive()) _environment.PrimeChat(e.KeyCode);
            }
            _environment.KeyboardHooks.TryGetValue(e.KeyCode, out hook);
        }

        if (hook != null && !HotKeySettingModel.ShouldBlockCloudHotkey(hook.ConfigPropertyName, cloudOnly))
        {
            // 不持有监听生命周期锁调用用户回调（回调可以停止监听或切换 UI）。
            if (down) hook.KeyDown(sender, e, cloudOnly, () => CanDispatch(session));
            else hook.KeyUp(sender, e, cloudOnly, () => CanDispatch(session));
        }

        if (cloudOnly) return;
        lock (_stateLock)
        {
            // 快捷键本身可能已退订/切换监听，不得由同一次事件重新启用连发。
            if (!IsCurrent(session) || _mode != ListeningMode.Local) return;
            if (e.KeyCode == _releaseControlKey)
                UpdateRepeat(down, ref _firstSpaceKeyDownTime, _spaceTimer, 300, _environment.SpaceRepeatEnabled);
            else if (e.KeyCode == _pickUpKey)
                UpdateRepeat(down, ref _firstFKeyDownTime, _fTimer, 200, _environment.FRepeatEnabled);
        }
    }

    private void UpdateRepeat(bool down, ref DateTime firstDown, IHotkeyRepeatTimer? timer, int threshold, Func<bool> enabled)
    {
        if (!down)
        {
            firstDown = DateTime.MaxValue;
            timer?.Stop();
        }
        else if (firstDown == DateTime.MaxValue)
        {
            firstDown = _environment.Now();
        }
        else if ((_environment.Now() - firstDown).TotalMilliseconds > threshold && enabled() && timer?.Enabled == false)
        {
            timer.Start();
        }
    }

    private void HandleMouse(long session, object? sender, MouseEventExtArgs e, MouseMonitorEvent kind)
    {
        MouseHook? hook;
        bool cloudOnly;
        lock (_stateLock)
        {
            if (!IsCurrent(session) || _environment.IsInputDisabled()) return;
            cloudOnly = _mode == ListeningMode.CloudHotkeys;
            if (!cloudOnly) _environment.RecordMouse(e, kind);
            if (kind is MouseMonitorEvent.Move or MouseMonitorEvent.Wheel) return;
            if (e.Button == MouseButtons.Left || (cloudOnly && e.Button is not (MouseButtons.XButton1 or MouseButtons.XButton2))) return;
            _environment.MouseHooks.TryGetValue(e.Button, out hook);
        }

        if (hook == null || HotKeySettingModel.ShouldBlockCloudHotkey(hook.ConfigPropertyName, cloudOnly)) return;
        if (kind == MouseMonitorEvent.Down) hook.MouseDown(sender, e, cloudOnly, () => CanDispatch(session));
        else hook.MouseUp(sender, e, cloudOnly, () => CanDispatch(session));
    }

    public void Unsubscribe()
    {
        lock (_stateLock) StopListening();
    }

    // 所有引用先失效，再逐个清理；其中一个 Dispose 失败也不会遗留其他监听或 timer。
    private void StopListening()
    {
        _mode = ListeningMode.None;
        ++_session;
        _firstFKeyDownTime = DateTime.MaxValue;
        _firstSpaceKeyDownTime = DateTime.MaxValue;
        foreach (var hook in _environment.KeyboardHooks.Values.ToArray()) hook.ResetPressedState();
        foreach (var hook in _environment.MouseHooks.Values.ToArray()) hook.ResetPressedState();

        var subscription = _subscription;
        var fTimer = _fTimer;
        var spaceTimer = _spaceTimer;
        _subscription = null;
        _fTimer = null;
        _spaceTimer = null;
        List<Exception>? errors = null;
        void Cleanup(Action action)
        {
            try { action(); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        if (fTimer != null)
        {
            Cleanup(fTimer.Stop);
            Cleanup(fTimer.Dispose);
        }
        if (spaceTimer != null)
        {
            Cleanup(spaceTimer.Stop);
            Cleanup(spaceTimer.Dispose);
        }
        Cleanup(DisposeWineAddon);
        if (subscription != null) Cleanup(subscription.Dispose);
        if (errors != null) throw new AggregateException(errors);
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            try { StopListening(); }
            finally { GC.SuppressFinalize(this); }
        }
    }

    // 每次订阅捕获独立 session，旧事件即使已经排队也无法进入新一轮本地监听。
    private sealed class EventSubscription : IDisposable
    {
        private readonly MouseKeyMonitor _owner;
        private readonly IKeyboardMouseEvents _source;
        private readonly KeyEventHandler _keyDown;
        private readonly KeyEventHandler _keyUp;
        private readonly EventHandler<MouseEventExtArgs> _mouseDown;
        private readonly EventHandler<MouseEventExtArgs> _mouseUp;
        private readonly EventHandler<MouseEventExtArgs> _mouseMove;
        private readonly EventHandler<MouseEventExtArgs> _mouseWheel;

        internal EventSubscription(MouseKeyMonitor owner, IKeyboardMouseEvents source, long session)
        {
            _owner = owner;
            _source = source;
            _keyDown = (s, e) => owner.HandleKey(session, s, e, true);
            _keyUp = (s, e) => owner.HandleKey(session, s, e, false);
            _mouseDown = (s, e) => owner.HandleMouse(session, s, e, MouseMonitorEvent.Down);
            _mouseUp = (s, e) => owner.HandleMouse(session, s, e, MouseMonitorEvent.Up);
            _mouseMove = (s, e) => owner.HandleMouse(session, s, e, MouseMonitorEvent.Move);
            _mouseWheel = (s, e) => owner.HandleMouse(session, s, e, MouseMonitorEvent.Wheel);
        }

        internal void Attach()
        {
            _source.KeyDown += _keyDown;
            _source.KeyUp += _keyUp;
            _source.MouseDownExt += _mouseDown;
            _source.MouseUpExt += _mouseUp;
            _source.MouseMoveExt += _mouseMove;
            _source.MouseWheelExt += _mouseWheel;
        }

        public void Dispose()
        {
            List<Exception>? errors = null;
            void Cleanup(Action action)
            {
                try { action(); }
                catch (Exception error) { (errors ??= []).Add(error); }
            }
            Cleanup(() => _source.KeyDown -= _keyDown);
            Cleanup(() => _source.KeyUp -= _keyUp);
            Cleanup(() => _source.MouseDownExt -= _mouseDown);
            Cleanup(() => _source.MouseUpExt -= _mouseUp);
            Cleanup(() => _source.MouseMoveExt -= _mouseMove);
            Cleanup(() => _source.MouseWheelExt -= _mouseWheel);
            Cleanup(() => _owner._environment.ReleaseEventSource(_source));
            if (errors != null) throw new AggregateException(errors);
        }
    }
}
