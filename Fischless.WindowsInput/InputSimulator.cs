namespace Fischless.WindowsInput;

public class InputSimulator : IInputSimulator
{
    /// <summary>应用层可暂停桌面输入；返回 false 时不调用系统输入 API。</summary>
    public static Func<bool>? InputDispatchGuard { get; set; }

    public InputSimulator(IKeyboardSimulator keyboardSimulator, IMouseSimulator mouseSimulator, IInputDeviceStateAdaptor inputDeviceStateAdaptor)
    {
        _keyboardSimulator = keyboardSimulator;
        _mouseSimulator = mouseSimulator;
        _inputDeviceState = inputDeviceStateAdaptor;
    }

    public InputSimulator()
    {
        _keyboardSimulator = new KeyboardSimulator(this);
        _mouseSimulator = new MouseSimulator(this);
        _inputDeviceState = new WindowsInputDeviceStateAdaptor();
    }

    public IKeyboardSimulator Keyboard => _keyboardSimulator;

    public IMouseSimulator Mouse => _mouseSimulator;

    public IInputDeviceStateAdaptor InputDeviceState => _inputDeviceState;

    private readonly IKeyboardSimulator _keyboardSimulator;

    private readonly IMouseSimulator _mouseSimulator;

    private readonly IInputDeviceStateAdaptor _inputDeviceState;
}
