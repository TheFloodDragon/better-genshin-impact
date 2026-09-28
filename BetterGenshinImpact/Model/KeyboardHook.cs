using BetterGenshinImpact.GameTask;
using Fischless.HotkeyCapture;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Vanara.PInvoke;

namespace BetterGenshinImpact.Model;

public class KeyboardHook
{
    public static Dictionary<Keys, KeyboardHook> AllKeyboardHooks = [];
    public event EventHandler<KeyPressedEventArgs>? KeyPressedEvent;
    public event EventHandler<KeyPressedEventArgs>? KeyDownEvent;
    public event EventHandler<KeyPressedEventArgs>? KeyUpEvent;
    public bool IsHold { get; set; }
    public Keys BindKey { get; set; } = Keys.None;
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

    public KeyboardHook() : this(SystemControl.IsGenshinImpactActive, ChatUiHotkeyGuard.ShouldBlockHotkey) { }

    internal KeyboardHook(Func<bool> isGameActive, Func<string?, bool> shouldBlockHotkey, Action<Action>? queueAction = null)
    {
        _isGameActive = isGameActive;
        _shouldBlockHotkey = shouldBlockHotkey;
        _queueAction = queueAction ?? (action => { _ = Task.Run(action); });
    }

    /// <summary>长按时系统会重复触发 KeyDown。</summary>
    public void KeyDown(object? sender, KeyEventArgs e) => KeyDown(sender, e, false);

    internal void KeyDown(object? sender, KeyEventArgs e, bool cloudOnly, Func<bool>? isCurrent = null)
    {
        if (!_isGameActive() || e.KeyCode != BindKey
            || HotKeySettingModel.ShouldBlockCloudHotkey(ConfigPropertyName, cloudOnly)
            || (!cloudOnly && _shouldBlockHotkey(ConfigPropertyName)) || isCurrent?.Invoke() == false) return;

        var generation = Volatile.Read(ref _pressGeneration);
        IsPressed = true;
        KeyDownEvent?.Invoke(this, new KeyPressedEventArgs(User32.HotKeyModifiers.MOD_NONE, e.KeyCode));
        if (!IsCurrentPress(generation) || isCurrent?.Invoke() == false) return;
        if (IsHold && !cloudOnly)
        {
            if (KeyPressedEvent != null) _queueAction(() => RunAction(e, generation, isCurrent));
        }
        else
        {
            try { KeyPressedEvent?.Invoke(this, new KeyPressedEventArgs(User32.HotKeyModifiers.MOD_NONE, e.KeyCode)); }
            finally { IsPressed = false; }
        }
    }

    private bool IsCurrentPress(int generation) => IsPressed && generation == Volatile.Read(ref _pressGeneration);

    private void RunAction(KeyEventArgs e, int generation, Func<bool>? isCurrent)
    {
        lock (this)
        {
            // 单纯清空 IsPressed 不足以作废排队中的长按：下一次按下会把它重新置 true。
            while (IsCurrentPress(generation) && KeyPressedEvent != null && isCurrent?.Invoke() != false)
            {
                if (_shouldBlockHotkey(ConfigPropertyName))
                {
                    Thread.Sleep(10);
                    continue;
                }
                KeyPressedEvent?.Invoke(this, new KeyPressedEventArgs(User32.HotKeyModifiers.MOD_NONE, e.KeyCode));
            }
        }
    }

    public void KeyUp(object? sender, KeyEventArgs e) => KeyUp(sender, e, false);

    internal void KeyUp(object? sender, KeyEventArgs e, bool cloudOnly, Func<bool>? isCurrent = null)
    {
        if (e.KeyCode != BindKey) return;
        ResetPressedState();
        if (_isGameActive() && !HotKeySettingModel.ShouldBlockCloudHotkey(ConfigPropertyName, cloudOnly)
            && (cloudOnly || !_shouldBlockHotkey(ConfigPropertyName)) && isCurrent?.Invoke() != false)
            KeyUpEvent?.Invoke(this, new KeyPressedEventArgs(User32.HotKeyModifiers.MOD_NONE, e.KeyCode));
    }

    internal void ResetPressedState() => IsPressed = false;

    public void RegisterHotKey(Keys key)
    {
        BindKey = key;
        AllKeyboardHooks.Add(key, this);
    }

    public void UnregisterHotKey()
    {
        ResetPressedState();
        IsHold = false;
        if (AllKeyboardHooks.TryGetValue(BindKey, out var hook) && ReferenceEquals(hook, this))
            AllKeyboardHooks.Remove(BindKey);
    }

    public void Dispose() => UnregisterHotKey();
}
