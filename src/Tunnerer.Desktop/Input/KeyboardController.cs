namespace Tunnerer.Desktop.Input;

using Silk.NET.SDL;
using Tunnerer.Core.Input;
using Tunnerer.Core.Types;

public readonly record struct KeyBindings(
    Scancode Left, Scancode Right, Scancode Up, Scancode Down, Scancode Shoot, Scancode Build, Scancode BuildHarvester);

public unsafe class KeyboardController
{
    private readonly Sdl _sdl;
    private readonly KeyBindings _keys;

    public KeyboardController(Sdl sdl, KeyBindings keys)
    {
        _sdl = sdl;
        _keys = keys;
    }

    public bool IsLeftHeld { get { byte* k = _sdl.GetKeyboardState(null); return k[(int)_keys.Left] != 0; } }
    public bool IsRightHeld { get { byte* k = _sdl.GetKeyboardState(null); return k[(int)_keys.Right] != 0; } }
    public bool IsUpHeld { get { byte* k = _sdl.GetKeyboardState(null); return k[(int)_keys.Up] != 0; } }
    public bool IsDownHeld { get { byte* k = _sdl.GetKeyboardState(null); return k[(int)_keys.Down] != 0; } }

    public ControllerOutput Poll()
    {
        byte* keys = _sdl.GetKeyboardState(null);
        int dx = 0, dy = 0;
        if (keys[(int)_keys.Left] != 0) dx -= 1;
        if (keys[(int)_keys.Right] != 0) dx += 1;
        if (keys[(int)_keys.Up] != 0) dy -= 1;
        if (keys[(int)_keys.Down] != 0) dy += 1;

        return new ControllerOutput
        {
            MoveSpeed = new Offset(dx, dy),
            ShootPrimary = keys[(int)_keys.Shoot] != 0,
            BuildPrimary = keys[(int)_keys.Build] != 0,
            BuildSecondary = keys[(int)_keys.BuildHarvester] != 0,
        };
    }
}
