namespace Tunnerer.Desktop.Input;

using static SDL3.SDL;

/// <summary>
/// Keeps the key that opened a text menu out of the menu's text. Text input is always on, so a key press arrives as a
/// key down and then as the character it types, and a key held down repeats both. While the opening key stays down,
/// its characters and repeats are not for the field that has the keyboard; the key coming up, or any other key going
/// down, ends that.
/// </summary>
public sealed class HeldKeyTextGuard
{
    private Scancode? _held;

    /// <summary>The menu was just opened by <paramref name="key"/>, which is down.</summary>
    public void Arm(Scancode key) => _held = key;

    /// <summary>The menu closed or was rebuilt: nothing is being held for it.</summary>
    public void Clear() => _held = null;

    /// <summary>A typed character: false while it belongs to the key that opened the menu.</summary>
    public bool AcceptsText => _held is null;

    /// <summary>A key went down: true when it is the opening key repeating, which the menu ignores.</summary>
    public bool SwallowsKeyDown(Scancode key)
    {
        if (_held is not { } held)
            return false;
        if (key == held)
            return true;
        _held = null;
        return false;
    }

    /// <summary>A key came up: the opening key being let go ends the guard.</summary>
    public void KeyUp(Scancode key)
    {
        if (key == _held)
            _held = null;
    }
}
