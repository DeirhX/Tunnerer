namespace Tunnerer.Desktop.Rendering;

[Flags]
public enum RenderKeyModifiers
{
    None = 0,
    Ctrl = 1 << 0,
    Shift = 1 << 1,
    Alt = 1 << 2,
}

public enum RenderInputEventType
{
    MouseMove,
    MouseButton,
    MouseWheel,
    TextInput,
    Key,
}

public enum RenderMouseButton
{
    Left = 0,
    Right = 1,
    Middle = 2,
    Unknown = -1,
}

public enum RenderKey
{
    None,
    Tab,
    LeftArrow,
    RightArrow,
    UpArrow,
    DownArrow,
    PageUp,
    PageDown,
    Home,
    End,
    Insert,
    Delete,
    Backspace,
    Space,
    Enter,
    Escape,
    A,
    C,
    V,
    X,
    Y,
    Z,
}

public readonly record struct RenderInputEvent(
    RenderInputEventType Type,
    int MouseX = 0,
    int MouseY = 0,
    RenderMouseButton MouseButton = RenderMouseButton.Unknown,
    bool IsPressed = false,
    float WheelX = 0,
    float WheelY = 0,
    string? Text = null,
    RenderKey Key = RenderKey.None,
    RenderKeyModifiers Modifiers = RenderKeyModifiers.None,
    bool IsRepeat = false);
