using BetterGenshinImpact.GameTask;
using Fischless.HotkeyCapture;
using Gma.System.MouseKeyHook;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Vanara.PInvoke;

namespace BetterGenshinImpact.Model;

public class MouseHook
{
    public static Dictionary<MouseButtons, MouseHook> AllMouseHooks = [];
    public event EventHandler<KeyPressedEventArgs>? MousePressed;
    public event EventHandler<KeyPressedEventArgs>? MouseDownEvent;
    public event EventHandler<KeyPressedEventArgs>? MouseUpEvent;
    public bool IsHold { get; set; }
    public MouseButtons BindMouse { get; set; } = MouseButtons.Left;
    public string ConfigPropertyName { get; set; } = string.Empty;

    private volatile bool _isPressed;
    private int _pressGeneration;
    private readonly Func<bool> _isGameActive;
    private readonly Func<string?, bool> _shouldBlockHotkey;
    private readonly Action<Action> _queueAction;

    public bool IsPressed
    {
        get => _isPressed;
        set
        {
            if (!value) Interlocked.Increment(ref _pressGeneration);
            _isPressed = value;
        }
    }

    public MouseHook() : this(SystemControl.IsGenshinImpactActive, ChatUiHotkeyGuard.ShouldBlockHotkey) { }

    internal MouseHook(Func<bool> isGameActive, Func<string?, bool> shouldBlockHotkey, Action<Action>? queueAction = null)
    {
        _isGameActive = isGameActive;
        _shouldBlockHotkey = shouldBlockHotkey;
        _queueAction = queueAction ?? (action => { _ = Task.Run(action); });
    }

    public void MouseDown(object? sender, MouseEventExtArgs e) => MouseDown(sender, e, false);

    internal void MouseDown(object? sender, MouseEventExtArgs e, bool cloudOnly, Func<bool>? isCurrent = null)
    {
        if (!_isGameActive() || e.Button is MouseButtons.Left or MouseButtons.None || e.Button != BindMouse
            || HotKeySettingModel.ShouldBlockCloudHotkey(ConfigPropertyName, cloudOnly)
            || (!cloudOnly && _shouldBlockHotkey(ConfigPropertyName)) || isCurrent?.Invoke() == false) return;

        var generation = Volatile.Read(ref _pressGeneration);
        IsPressed = true;
        MouseDownEvent?.Invoke(this, new KeyPressedEventArgs(User32.HotKeyModifiers.MOD_NONE, Keys.None));
        if (!IsCurrentPress(generation) || isCurrent?.Invoke() == false) return;
        if (IsHold && !cloudOnly)
        {
            _queueAction(() => RunAction(generation, isCurrent));
        }
        else
        {
            try { MousePressed?.Invoke(this, new KeyPressedEventArgs(User32.HotKeyModifiers.MOD_NONE, Keys.None)); }
            finally { IsPressed = false; }
        }
    }

    private bool IsCurrentPress(int generation) => IsPressed && generation == Volatile.Read(ref _pressGeneration);

    private void RunAction(int generation, Func<bool>? isCurrent)
    {
        lock (this)
        {
            while (IsCurrentPress(generation) && isCurrent?.Invoke() != false)
            {
                if (_shouldBlockHotkey(ConfigPropertyName))
                {
                    Thread.Sleep(10);
                    continue;
                }
                MousePressed?.Invoke(this, new KeyPressedEventArgs(User32.HotKeyModifiers.MOD_NONE, Keys.None));
            }
        }
    }

    public void MouseUp(object? sender, MouseEventExtArgs e) => MouseUp(sender, e, false);

    internal void MouseUp(object? sender, MouseEventExtArgs e, bool cloudOnly, Func<bool>? isCurrent = null)
    {
        if (e.Button is MouseButtons.Left or MouseButtons.None || e.Button != BindMouse) return;
        ResetPressedState();
        if (_isGameActive() && !HotKeySettingModel.ShouldBlockCloudHotkey(ConfigPropertyName, cloudOnly)
            && (cloudOnly || !_shouldBlockHotkey(ConfigPropertyName)) && isCurrent?.Invoke() != false)
            MouseUpEvent?.Invoke(this, new KeyPressedEventArgs(User32.HotKeyModifiers.MOD_NONE, Keys.None));
    }

    internal void ResetPressedState() => IsPressed = false;

    public void RegisterHotKey(MouseButtons mouseButton)
    {
        BindMouse = mouseButton;
        AllMouseHooks.Add(mouseButton, this);
    }

    public void UnregisterHotKey()
    {
        ResetPressedState();
        IsHold = false;
        if (AllMouseHooks.TryGetValue(BindMouse, out var hook) && ReferenceEquals(hook, this))
            AllMouseHooks.Remove(BindMouse);
    }

    public void Dispose() => UnregisterHotKey();
}
