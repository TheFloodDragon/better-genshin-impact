using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Monitor;
using BetterGenshinImpact.Model;
using Gma.System.MouseKeyHook;
using System.Reflection;
using System.Windows.Forms;
using Vanara.PInvoke;

namespace BetterGenshinImpact.UnitTest.CoreTests.MonitorTests;

public class CloudHotkeyMonitorTests
{
    [Theory]
    [InlineData(nameof(HotKeyConfig.BgiEnabledHotkey))]
    [InlineData(nameof(HotKeyConfig.CancelTaskHotkey))]
    [InlineData(nameof(HotKeyConfig.SuspendHotkey))]
    [InlineData(nameof(HotKeyConfig.TakeScreenshotHotkey))]
    [InlineData(nameof(HotKeyConfig.LogBoxDisplayHotkey))]
    [InlineData(nameof(HotKeyConfig.OverlayMetricsDisplayHotkey))]
    public void CloudEvents_ReachSafeKeyboardAndBothSideButtons(string action)
    {
        using var fixture = new Fixture();
        fixture.AddKeyboard(Keys.K, action);
        fixture.AddMouse(MouseButtons.XButton1, action);
        fixture.AddMouse(MouseButtons.XButton2, action);
        fixture.Monitor.SubscribeCloudHotkeys(123);

        fixture.Source.Key(Keys.K, true);
        fixture.Source.Key(Keys.K, false);
        fixture.Source.Mouse(MouseButtons.XButton1, MouseMonitorEvent.Down);
        fixture.Source.Mouse(MouseButtons.XButton1, MouseMonitorEvent.Up);
        fixture.Source.Mouse(MouseButtons.XButton2, MouseMonitorEvent.Down);
        fixture.Source.Mouse(MouseButtons.XButton2, MouseMonitorEvent.Up);

        Assert.Equal(9, fixture.CallbackCount); // Down、Press、Up 各一次。
        Assert.Equal(0, fixture.RecordCount);
        Assert.Equal(0, fixture.PrimeCount);
        Assert.Equal(0, fixture.SettingsReadCount);
        Assert.Empty(fixture.Timers);
        Assert.Empty(fixture.PendingHolds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("UnknownAction")]
    [InlineData(nameof(HotKeyConfig.OneKeyFightHotkey))]
    public void CloudEvents_BlockUnknownAndUnsafeActions_ButLocalRoutingIsRestored(string? action)
    {
        using var fixture = new Fixture();
        fixture.AddKeyboard(Keys.K, action!);
        fixture.AddMouse(MouseButtons.XButton1, action!);
        fixture.Monitor.SubscribeCloudHotkeys(123);
        fixture.PressBoth();
        Assert.Equal(0, fixture.CallbackCount);
        Assert.Equal(0, fixture.RecordCount);

        fixture.Monitor.Unsubscribe();
        fixture.Monitor.Subscribe(456);
        fixture.PressBoth();
        fixture.Source.Mouse(MouseButtons.None, MouseMonitorEvent.Move);
        fixture.Source.Mouse(MouseButtons.None, MouseMonitorEvent.Wheel);
        Assert.Equal(6, fixture.CallbackCount);
        Assert.Equal(6, fixture.RecordCount);
        Assert.Equal(1, fixture.PrimeCount);
        Assert.Equal(1, fixture.SettingsReadCount);
    }

    [Fact]
    public void CloudEvents_DoNotEnterRecordingChatPrimingOrRepeatBranches()
    {
        using var fixture = new Fixture();
        fixture.AddKeyboard(Keys.F, nameof(HotKeyConfig.OneKeyFightHotkey), hold: true);
        fixture.AddKeyboard(Keys.Space, "UnsafeJump", hold: true);
        fixture.AddMouse(MouseButtons.XButton1, "UnsafeMouseMacro", hold: true);
        fixture.Monitor.SubscribeCloudHotkeys(123);
        fixture.ThrowOnLocalAccess = true;

        for (var i = 0; i < 3; i++)
        {
            fixture.Source.Key(Keys.F, true);
            fixture.Source.Key(Keys.Space, true);
            fixture.Advance();
        }
        fixture.Source.Key(Keys.F, false);
        fixture.Source.Key(Keys.Space, false);
        fixture.Source.Mouse(MouseButtons.XButton1, MouseMonitorEvent.Down);
        fixture.Source.Mouse(MouseButtons.XButton1, MouseMonitorEvent.Up);
        fixture.Source.Mouse(MouseButtons.None, MouseMonitorEvent.Move);
        fixture.Source.Mouse(MouseButtons.None, MouseMonitorEvent.Wheel);

        Assert.Equal(0, fixture.CallbackCount);
        Assert.Equal(0, fixture.InputCount);
        Assert.Equal(0, fixture.RecordCount);
        Assert.Equal(0, fixture.PrimeCount);
        Assert.Empty(fixture.Timers);
        Assert.Empty(fixture.PendingHolds);
    }

    [Fact]
    public void CloudMonitor_PreservesForegroundRequirement()
    {
        using var fixture = new Fixture();
        fixture.AddKeyboard(Keys.K, nameof(HotKeyConfig.CancelTaskHotkey));
        fixture.AddMouse(MouseButtons.XButton1, nameof(HotKeyConfig.TakeScreenshotHotkey));
        fixture.Monitor.SubscribeCloudHotkeys(123);
        fixture.Focused = false;
        fixture.PressBoth();
        Assert.Equal(0, fixture.CallbackCount);
        fixture.Focused = true;
        fixture.PressBoth();
        Assert.Equal(6, fixture.CallbackCount);
    }

    [Fact]
    public void CloudSafetyCallbacks_IgnoreStaleLocalChatState_WithoutChangingLocalGuard()
    {
        using var fixture = new Fixture { ChatBlocked = true };
        fixture.AddKeyboard(Keys.K, nameof(HotKeyConfig.CancelTaskHotkey));
        fixture.AddMouse(MouseButtons.XButton1, nameof(HotKeyConfig.CancelTaskHotkey));
        fixture.Monitor.SubscribeCloudHotkeys(123);
        fixture.PressBoth();
        Assert.Equal(6, fixture.CallbackCount);
        fixture.Monitor.Subscribe(456);
        fixture.PressBoth();
        Assert.Equal(6, fixture.CallbackCount);
    }

    [Fact]
    public void DisabledMonitor_DoesNotInstallHook_AndDisablesExistingSubscription()
    {
        using var fixture = new Fixture { Disabled = true };
        fixture.Monitor.SubscribeCloudHotkeys(123);
        fixture.Monitor.Subscribe(123);
        Assert.Empty(fixture.Sources);
        Assert.Empty(fixture.Timers);
        Assert.Equal(0, fixture.SettingsReadCount);

        fixture.Disabled = false;
        fixture.AddKeyboard(Keys.K, nameof(HotKeyConfig.CancelTaskHotkey));
        fixture.Monitor.SubscribeCloudHotkeys(123);
        fixture.Disabled = true;
        fixture.Source.Key(Keys.K, true);
        Assert.Equal(0, fixture.CallbackCount);
        fixture.Monitor.SubscribeCloudHotkeys(123);
        Assert.Equal(0, fixture.Source.HandlerCount);
        Assert.Equal(1, fixture.Source.DisposeCount);
    }

    [Fact]
    public void RepeatedStartStopAndDispose_DoNotDuplicateHandlers_OrReviveQueuedEvents()
    {
        using var fixture = new Fixture();
        fixture.AddKeyboard(Keys.K, nameof(HotKeyConfig.CancelTaskHotkey));
        for (var i = 0; i < 3; i++)
        {
            fixture.Monitor.SubscribeCloudHotkeys(123);
            fixture.Monitor.SubscribeCloudHotkeys(123);
            Assert.Equal(6, fixture.Source.HandlerCount);
            fixture.Source.Key(Keys.K, true);
            var queuedEvent = fixture.Source.CaptureKeyDown();
            fixture.Monitor.Unsubscribe();
            fixture.Monitor.Unsubscribe();
            Assert.Equal(0, fixture.Source.HandlerCount);
            Assert.Equal(1, fixture.Source.DisposeCount);
            fixture.Monitor.SubscribeCloudHotkeys(123);
            queuedEvent?.Invoke(null, new KeyEventArgs(Keys.K));
            Assert.Equal((i + 1) * 2, fixture.CallbackCount);
            fixture.Monitor.Unsubscribe();
        }
        fixture.Monitor.Dispose();
        fixture.Monitor.Dispose();
        fixture.Monitor.Unsubscribe();
        Assert.All(fixture.Sources, source => Assert.Equal(1, source.DisposeCount));
        Assert.Throws<ObjectDisposedException>(() => fixture.Monitor.SubscribeCloudHotkeys(123));
        Assert.Throws<ObjectDisposedException>(() => fixture.Monitor.Subscribe(123));
    }

    [Fact]
    public void PartialSubscriptionFailure_RollsBackAndCanRestart()
    {
        using var fixture = new Fixture { FailNextAdd = 4 };
        var hook = fixture.AddKeyboard(Keys.K, nameof(HotKeyConfig.CancelTaskHotkey));
        hook.IsPressed = true;
        Assert.Throws<InvalidOperationException>(() => fixture.Monitor.SubscribeCloudHotkeys(123));
        Assert.False(hook.IsPressed);
        Assert.Equal(0, fixture.Source.HandlerCount);
        Assert.Equal(1, fixture.Source.DisposeCount);

        fixture.Monitor.SubscribeCloudHotkeys(123);
        fixture.Source.Key(Keys.K, true);
        Assert.Equal(2, fixture.CallbackCount);
        Assert.Equal(2, fixture.Sources.Count);
        Assert.Equal(6, fixture.Source.HandlerCount);
    }

    [Fact]
    public void EventSourceCreationFailure_CleansLocalTimers_AndAllowsCloudRetry()
    {
        using var fixture = new Fixture { FailNextFactory = true };
        Assert.Throws<InvalidOperationException>(() => fixture.Monitor.Subscribe(123));
        Assert.Equal(2, fixture.Timers.Count);
        Assert.All(fixture.Timers, timer => Assert.Equal(1, timer.DisposeCount));
        fixture.Monitor.SubscribeCloudHotkeys(123);
        Assert.Equal(6, fixture.Source.HandlerCount);
        fixture.TickAll();
        Assert.Equal(0, fixture.InputCount);
    }

    [Fact]
    public void DisposeFailure_StillDetachesEveryHandlerAndAllowsRestart()
    {
        using var fixture = new Fixture();
        fixture.Monitor.SubscribeCloudHotkeys(123);
        var oldSource = fixture.Source;
        oldSource.FailRemove = "KeyUp";
        oldSource.FailDispose = true;
        Assert.Throws<AggregateException>(fixture.Monitor.Unsubscribe);
        Assert.Equal(0, oldSource.HandlerCount);
        Assert.Equal(1, oldSource.DisposeCount);
        fixture.Monitor.Unsubscribe();
        fixture.Monitor.SubscribeCloudHotkeys(123);
        Assert.Equal(6, fixture.Source.HandlerCount);
    }

    [Fact]
    public void ModeSwitch_StopsBothRepeatTimers_AndRejectsTheirQueuedCallbacksAfterLocalRestart()
    {
        using var fixture = new Fixture();
        fixture.Monitor.Subscribe(123);
        fixture.StartRepeats();
        var oldTimers = fixture.Timers.ToArray();
        Assert.All(oldTimers, timer => Assert.True(timer.Enabled));
        fixture.TickAll();
        Assert.Equal(2, fixture.InputCount);

        fixture.Monitor.SubscribeCloudHotkeys(456);
        Assert.All(oldTimers, timer => Assert.False(timer.Enabled));
        fixture.TickAll();
        Assert.Equal(2, fixture.InputCount);
        fixture.Monitor.Subscribe(789);
        fixture.TickAll();
        Assert.Equal(2, fixture.InputCount);
        fixture.StartRepeats();
        fixture.TickAll();
        Assert.Equal(4, fixture.InputCount);
        Assert.Equal((nint)789, fixture.LastInputHandle);
    }

    [Fact]
    public void OldKeyboardAndMouseHoldWorkers_DoNotRunAfterReturningToLocal()
    {
        using var fixture = new Fixture();
        var keyboard = fixture.AddKeyboard(Keys.K, "UnsafeKeyboardHold", hold: true);
        var mouse = fixture.AddMouse(MouseButtons.XButton1, "UnsafeMouseHold", hold: true);
        fixture.Monitor.Subscribe(123);
        fixture.Source.Key(Keys.K, true);
        fixture.Source.Mouse(MouseButtons.XButton1, MouseMonitorEvent.Down);
        var oldWorkers = fixture.PendingHolds.ToArray();
        Assert.Equal(2, oldWorkers.Length);
        fixture.PendingHolds.Clear();

        fixture.Monitor.SubscribeCloudHotkeys(456);
        Assert.False(keyboard.IsPressed);
        Assert.False(mouse.IsPressed);
        fixture.Monitor.Subscribe(789);
        fixture.Source.Key(Keys.K, true);
        fixture.Source.Mouse(MouseButtons.XButton1, MouseMonitorEvent.Down);
        Assert.True(keyboard.IsPressed);
        Assert.True(mouse.IsPressed);
        var before = fixture.CallbackCount;
        // 每次 Press 回调都结束假长按，即使发生回归也不会让测试陷入死循环。
        foreach (var worker in oldWorkers) worker();
        Assert.Equal(before, fixture.CallbackCount);
        foreach (var worker in fixture.PendingHolds) worker();
        Assert.Equal(before + 2, fixture.CallbackCount);
    }

    [Fact]
    public void CallbackCanSwitchMode_WithoutContinuingTheOldKeyDown()
    {
        using var fixture = new Fixture();
        var keyboard = fixture.AddKeyboard(Keys.F, "UnsafeMacro");
        keyboard.KeyDownEvent += (_, _) => fixture.Monitor.SubscribeCloudHotkeys(456);
        fixture.Monitor.Subscribe(123);
        fixture.Source.Key(Keys.F, true);
        fixture.TickAll();
        Assert.Equal(1, fixture.CallbackCount); // 只允许切换前已进入的 Down，不能再执行 Press。
        Assert.False(keyboard.IsPressed);
        Assert.Equal(0, fixture.InputCount);
        Assert.All(fixture.Timers, timer => Assert.False(timer.Enabled));
    }

    [Fact]
    public void QueuedLocalEvent_IsRejectedAfterSwitchingToCloud()
    {
        using var fixture = new Fixture();
        fixture.AddKeyboard(Keys.K, "UnsafeMacro");
        fixture.Monitor.Subscribe(123);
        var queuedEvent = fixture.Source.CaptureKeyDown();
        fixture.Monitor.SubscribeCloudHotkeys(456);
        fixture.ThrowOnLocalAccess = true;
        queuedEvent?.Invoke(null, new KeyEventArgs(Keys.K));
        Assert.Equal(0, fixture.CallbackCount);
        Assert.Equal(0, fixture.RecordCount);
        Assert.Equal(0, fixture.PrimeCount);
    }

    [Fact]
    public void WineCloudMode_DoesNotCreateNativeHooksOrRepeatTimers()
    {
        using var fixture = new Fixture { Wine = true };
        fixture.Monitor.SubscribeCloudHotkeys(123);
        fixture.Monitor.SubscribeCloudHotkeys(123);
        Assert.Empty(fixture.Sources);
        Assert.Empty(fixture.Timers);
        Assert.Equal(0, fixture.SettingsReadCount);
    }

    [Fact]
    public async Task CloudSwitch_WaitsForInFlightRepeat_ThenRejectsQueuedTicks()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        var switching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeInput = () =>
        {
            // 使用专用后台线程模拟在途回调，避免与其他同步宏测试争用线程池。
            Assert.True(Thread.CurrentThread.IsBackground);
            entered.TrySetResult();
            release.Wait();
        };
        fixture.Monitor.Subscribe(123);
        fixture.StartRepeats();
        var tickTask = Task.Factory.StartNew(fixture.Timers[0].QueuedTick,
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task? switchTask = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            switchTask = Task.Factory.StartNew(() =>
            {
                switching.SetResult();
                fixture.Monitor.SubscribeCloudHotkeys(456);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await switching.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(switchTask.IsCompleted);
            release.Set();
            await Task.WhenAll(tickTask, switchTask).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, fixture.InputCount);
            fixture.TickAll();
            Assert.Equal(1, fixture.InputCount);
        }
        finally
        {
            release.Set();
            await tickTask;
            if (switchTask != null) await switchTask;
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Dictionary<Keys, KeyboardHook> KeyboardHooks = [];
        internal readonly Dictionary<MouseButtons, MouseHook> MouseHooks = [];
        internal readonly List<FakeEventSource> Sources = [];
        internal readonly List<FakeTimer> Timers = [];
        internal readonly List<Action> PendingHolds = [];
        internal readonly MouseKeyMonitor Monitor;
        internal FakeEventSource Source => Sources[^1];
        internal bool Disabled, Wine, ThrowOnLocalAccess, FailNextFactory, ChatBlocked;
        internal bool Focused = true;
        internal int FailNextAdd, CallbackCount, RecordCount, PrimeCount, SettingsReadCount, InputCount;
        internal nint LastInputHandle;
        internal Action? BeforeInput;
        private DateTime _now = new(2025, 1, 1);

        internal Fixture()
        {
            Monitor = new MouseKeyMonitor(new MouseKeyMonitorEnvironment
            {
                CreateEventSource = () =>
                {
                    if (FailNextFactory)
                    {
                        FailNextFactory = false;
                        throw new InvalidOperationException("fake source failure");
                    }
                    var events = DispatchProxy.Create<IKeyboardMouseEvents, FakeEventSource>();
                    var source = (FakeEventSource)events;
                    source.FailAddNumber = FailNextAdd;
                    FailNextAdd = 0;
                    Sources.Add(source);
                    return events;
                },
                ReleaseEventSource = source => source.Dispose(),
                IsInputDisabled = () => Disabled,
                IsWine = () => Wine,
                GetSettings = () =>
                {
                    LocalAccess();
                    SettingsReadCount++;
                    return new MouseKeyMonitorSettings(Keys.F, User32.VK.VK_F, Keys.Space, User32.VK.VK_SPACE, 100, 100);
                },
                FRepeatEnabled = () => { LocalAccess(); return true; },
                SpaceRepeatEnabled = () => { LocalAccess(); return true; },
                IsGameActive = () => Focused,
                Now = () => _now,
                PrimeChat = _ => { LocalAccess(); PrimeCount++; },
                RecordKey = (_, _) => { LocalAccess(); RecordCount++; },
                RecordMouse = (_, _) => { LocalAccess(); RecordCount++; },
                SendRepeat = (handle, _) => { BeforeInput?.Invoke(); InputCount++; LastInputHandle = handle; },
                CreateRepeatTimer = (_, callback) =>
                {
                    LocalAccess();
                    var timer = new FakeTimer(callback);
                    Timers.Add(timer);
                    return timer;
                },
                KeyboardHooks = KeyboardHooks,
                MouseHooks = MouseHooks
            });
        }

        private void LocalAccess()
        {
            if (ThrowOnLocalAccess) throw new InvalidOperationException("Cloud event entered a local-only path");
        }

        internal KeyboardHook AddKeyboard(Keys key, string action, bool hold = false)
        {
            var hook = new KeyboardHook(() => Focused, _ => ChatBlocked, PendingHolds.Add)
            { BindKey = key, ConfigPropertyName = action, IsHold = hold };
            hook.KeyDownEvent += (_, _) => CallbackCount++;
            hook.KeyUpEvent += (_, _) => CallbackCount++;
            hook.KeyPressedEvent += (_, _) => { CallbackCount++; if (hold) hook.ResetPressedState(); };
            KeyboardHooks.Add(key, hook);
            return hook;
        }

        internal MouseHook AddMouse(MouseButtons button, string action, bool hold = false)
        {
            var hook = new MouseHook(() => Focused, _ => ChatBlocked, PendingHolds.Add)
            { BindMouse = button, ConfigPropertyName = action, IsHold = hold };
            hook.MouseDownEvent += (_, _) => CallbackCount++;
            hook.MouseUpEvent += (_, _) => CallbackCount++;
            hook.MousePressed += (_, _) => { CallbackCount++; if (hold) hook.ResetPressedState(); };
            MouseHooks.Add(button, hook);
            return hook;
        }

        internal void PressBoth()
        {
            Source.Key(Keys.K, true);
            Source.Key(Keys.K, false);
            Source.Mouse(MouseButtons.XButton1, MouseMonitorEvent.Down);
            Source.Mouse(MouseButtons.XButton1, MouseMonitorEvent.Up);
        }

        internal void Advance() => _now = _now.AddSeconds(1);
        internal void StartRepeats()
        {
            Source.Key(Keys.F, true);
            Source.Key(Keys.Space, true);
            Advance();
            Source.Key(Keys.F, true);
            Source.Key(Keys.Space, true);
        }
        internal void TickAll() { foreach (var timer in Timers) timer.QueuedTick(); }
        public void Dispose() => Monitor.Dispose();
    }

    private sealed class FakeTimer(Action callback) : IHotkeyRepeatTimer
    {
        public bool Enabled { get; private set; }
        internal int DisposeCount;
        public void Start() => Enabled = true;
        public void Stop() => Enabled = false;
        public void Dispose() { Enabled = false; DisposeCount++; }
        // 故意不检查 Enabled/Dispose，模拟已排队的 System.Timers.Timer.Elapsed。
        internal void QueuedTick() => callback();
    }

    public class FakeEventSource : DispatchProxy
    {
        private readonly Dictionary<string, Delegate?> _handlers = [];
        private int _adds;
        internal int FailAddNumber, DisposeCount;
        internal bool FailDispose;
        internal string? FailRemove;
        internal int HandlerCount => _handlers.Values.Sum(handler => handler?.GetInvocationList().Length ?? 0);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod!.Name;
            if (name.StartsWith("add_", StringComparison.Ordinal))
            {
                var key = name[4..];
                _handlers.TryGetValue(key, out var previous);
                _handlers[key] = Delegate.Combine(previous, (Delegate)args![0]!);
                if (++_adds == FailAddNumber) throw new InvalidOperationException("fake event add failure");
            }
            else if (name.StartsWith("remove_", StringComparison.Ordinal))
            {
                var key = name[7..];
                _handlers.TryGetValue(key, out var previous);
                _handlers[key] = Delegate.Remove(previous, (Delegate)args![0]!);
                if (FailRemove == key) throw new InvalidOperationException("fake event remove failure");
            }
            else if (name == nameof(IDisposable.Dispose))
            {
                DisposeCount++;
                _handlers.Clear();
                if (FailDispose) throw new InvalidOperationException("fake source dispose failure");
            }
            else throw new NotSupportedException(name);
            return null;
        }

        internal KeyEventHandler? CaptureKeyDown() => _handlers.GetValueOrDefault("KeyDown") as KeyEventHandler;
        internal void Key(Keys key, bool down) =>
            (_handlers.GetValueOrDefault(down ? "KeyDown" : "KeyUp") as KeyEventHandler)?.Invoke(this, new KeyEventArgs(key));
        // MouseKeyHook 5.7.1 只提供内部八参数构造，反射仅限假事件源，避免安装系统钩子。
        private static readonly ConstructorInfo MouseArgsConstructor = typeof(MouseEventExtArgs)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();

        internal void Mouse(MouseButtons button, MouseMonitorEvent kind)
        {
            var point = Activator.CreateInstance(MouseArgsConstructor.GetParameters()[2].ParameterType)!;
            var args = (MouseEventExtArgs)MouseArgsConstructor.Invoke(
                [button, 1, point, 120, 0, kind == MouseMonitorEvent.Down, kind == MouseMonitorEvent.Up, false]);
            (_handlers.GetValueOrDefault($"Mouse{kind}Ext") as EventHandler<MouseEventExtArgs>)?.Invoke(this, args);
        }
    }
}
