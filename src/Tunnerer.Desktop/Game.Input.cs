namespace Tunnerer.Desktop;

using SDL3;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Config;
using Tunnerer.Desktop.Input;
using Tunnerer.Desktop.Rendering;
using System.Runtime.InteropServices;
using static SDL3.SDL;

public partial class Game
{
    private void HandleEvent(Event ev)
    {
        if (HandleSessionEvent(ev))
            return;

        if (TryMapRenderInputEvent(ev, out var renderEvent))
            _renderBackend.ProcessEvent(renderEvent);
        if (ev.Type != (uint)EventType.KeyDown)
            return;

        // Ignore key-repeat so toggles flip once per physical press.
        if (ev.Key.Repeat)
            return;

        Scancode scancode = ev.Key.Scancode;
        bool shiftHeld = (ev.Key.Mod & Keymod.Shift) != 0;

        // After a match ends, Enter starts a new one.
        if (scancode == Scancode.Return && !_terrainTestMode && _world.IsMatchOver)
        {
            ExecuteGameCommand(GameCommand.RestartMatch, InputCommandSources.Input);
            return;
        }

        if (GameCommandController.TryTranslateHotkey(scancode, shiftHeld, _commandController.ShowPostPassOverlay, out GameCommand command))
            ExecuteGameCommand(command, InputCommandSources.Input);
    }

    /// <summary>True when the player asked for a new match (Enter after a match ended).</summary>
    public bool RestartRequested { get; private set; }

    private static unsafe bool TryMapRenderInputEvent(Event ev, out RenderInputEvent renderEvent)
    {
        renderEvent = default;
        switch ((EventType)ev.Type)
        {
            case EventType.MouseMotion:
                renderEvent = new RenderInputEvent(
                    Type: RenderInputEventType.MouseMove,
                    MouseX: (int)ev.Motion.X,
                    MouseY: (int)ev.Motion.Y);
                return true;
            case EventType.MouseButtonDown:
            case EventType.MouseButtonUp:
                renderEvent = new RenderInputEvent(
                    Type: RenderInputEventType.MouseButton,
                    MouseButton: ev.Button.Button switch
                    {
                        1 => RenderMouseButton.Left,
                        2 => RenderMouseButton.Middle,
                        3 => RenderMouseButton.Right,
                        _ => RenderMouseButton.Unknown,
                    },
                    IsPressed: ev.Type == (uint)EventType.MouseButtonDown);
                return true;
            case EventType.MouseWheel:
                renderEvent = new RenderInputEvent(
                    Type: RenderInputEventType.MouseWheel,
                    WheelX: ev.Wheel.X,
                    WheelY: ev.Wheel.Y);
                return true;
            case EventType.TextInput:
                renderEvent = new RenderInputEvent(
                    Type: RenderInputEventType.TextInput,
                    Text: Marshal.PtrToStringUTF8(ev.Text.Text));
                return true;
            case EventType.KeyDown:
            case EventType.KeyUp:
                renderEvent = new RenderInputEvent(
                    Type: RenderInputEventType.Key,
                    Key: ToRenderKey(ev.Key.Scancode),
                    Modifiers: ToRenderModifiers(ev.Key.Mod),
                    IsPressed: ev.Type == (uint)EventType.KeyDown,
                    IsRepeat: ev.Key.Repeat);
                return true;
            default:
                return false;
        }
    }

    private static RenderKey ToRenderKey(Scancode scancode) => scancode switch
    {
        Scancode.Tab => RenderKey.Tab,
        Scancode.Left => RenderKey.LeftArrow,
        Scancode.Right => RenderKey.RightArrow,
        Scancode.Up => RenderKey.UpArrow,
        Scancode.Down => RenderKey.DownArrow,
        Scancode.Pageup => RenderKey.PageUp,
        Scancode.Pagedown => RenderKey.PageDown,
        Scancode.Home => RenderKey.Home,
        Scancode.End => RenderKey.End,
        Scancode.Insert => RenderKey.Insert,
        Scancode.Delete => RenderKey.Delete,
        Scancode.Backspace => RenderKey.Backspace,
        Scancode.Space => RenderKey.Space,
        Scancode.Return => RenderKey.Enter,
        Scancode.Escape => RenderKey.Escape,
        Scancode.A => RenderKey.A,
        Scancode.C => RenderKey.C,
        Scancode.V => RenderKey.V,
        Scancode.X => RenderKey.X,
        Scancode.Y => RenderKey.Y,
        Scancode.Z => RenderKey.Z,
        _ => RenderKey.None,
    };

    private static RenderKeyModifiers ToRenderModifiers(Keymod modifiers)
    {
        RenderKeyModifiers mapped = RenderKeyModifiers.None;
        if ((modifiers & Keymod.Ctrl) != 0)
            mapped |= RenderKeyModifiers.Ctrl;
        if ((modifiers & Keymod.Shift) != 0)
            mapped |= RenderKeyModifiers.Shift;
        if ((modifiers & Keymod.Alt) != 0)
            mapped |= RenderKeyModifiers.Alt;
        return mapped;
    }

    private void ExecuteGameCommand(GameCommand command, string source)
    {
        if (command == GameCommand.RestartMatch)
        {
            if (!_terrainTestMode && _world.IsMatchOver)
            {
                Console.WriteLine($"[Game] Restarting match [source={source}]");
                RestartRequested = true;
                _isRunning = false;
            }
            return;
        }
        if (HandleSessionCommand(command))
            return;

        RecordInputCommandForReplay(command, source);
        _commandController.Execute(
            command,
            source,
            requestScreenshot: label => _renderBackend.RequestScreenshot(label),
            toggleInputRecording: ToggleInputRecording);
    }

    private FrameInputSnapshot CaptureFrameInput()
    {
        var (mx, my, buttons) = _renderer.GetMouseState();
        return new FrameInputSnapshot(mx, my, buttons);
    }

    private DirectionF? ComputeAimDirection(IReadOnlyList<Core.Entities.Tank> tanks, in FrameInputSnapshot frameInput)
    {
        if (tanks.Count == 0) return null;
        var tank = tanks[0];

        if (!IsMouseInViewport(frameInput, out float relX, out float relY))
            return null;

        int scale = DesktopScreenTweaks.PixelScale;
        float aimWorldX = (_renderViewState.CamPixelX + relX) / scale;
        float aimWorldY = (_renderViewState.CamPixelY + relY) / scale;

        float dx = aimWorldX - tank.Position.X;
        float dy = aimWorldY - tank.Position.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.001f) return null;
        return new DirectionF(dx / len, dy / len);
    }

    private bool IsMouseInViewport(in FrameInputSnapshot frameInput, out float relX, out float relY)
    {
        relX = 0; relY = 0;
        float vpW = _renderViewState.HiResSize.X;
        float vpH = _renderViewState.HiResSize.Y;
        if (vpW <= 0 || vpH <= 0) return false;

        relX = frameInput.MouseX;
        relY = frameInput.MouseY;
        if (relX < 0 || relY < 0 || relX >= vpW || relY >= vpH)
            return false;

        return true;
    }
}
