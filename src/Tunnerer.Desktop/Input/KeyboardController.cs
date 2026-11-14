namespace Tunnerer.Desktop.Input;

using Tunnerer.Core.Input;
using Tunnerer.Core.Types;
using static SDL3.SDL;

public readonly record struct KeyBindings(
    Scancode Left, Scancode Right, Scancode Up, Scancode Down, Scancode Shoot, Scancode Build, Scancode BuildHarvester);

public class KeyboardController
{
    private readonly KeyBindings _keys;

    public KeyboardController(KeyBindings keys)
    {
        _keys = keys;
    }

    private static bool IsHeld(Scancode key)
    {
        ReadOnlySpan<bool> keys = GetKeyboardState(out int count);
        int index = (int)key;
        return index < count && keys[index];
    }

    public bool IsLeftHeld => IsHeld(_keys.Left);
    public bool IsRightHeld => IsHeld(_keys.Right);
    public bool IsUpHeld => IsHeld(_keys.Up);
    public bool IsDownHeld => IsHeld(_keys.Down);

    public ControllerOutput Poll()
    {
        int dx = 0, dy = 0;
        if (IsHeld(_keys.Left)) dx -= 1;
        if (IsHeld(_keys.Right)) dx += 1;
        if (IsHeld(_keys.Up)) dy -= 1;
        if (IsHeld(_keys.Down)) dy += 1;

        return new ControllerOutput
        {
            MoveSpeed = new Offset(dx, dy),
            ShootPrimary = IsHeld(_keys.Shoot),
            BuildPrimary = IsHeld(_keys.Build),
            BuildSecondary = IsHeld(_keys.BuildHarvester),
        };
    }
}
