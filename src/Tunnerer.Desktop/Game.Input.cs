namespace Tunnerer.Desktop;

using SDL3;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Config;
using Tunnerer.Desktop.Input;
using Tunnerer.Desktop.Rendering;
using Tunnerer.Core.Entities.Tanks;
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

        // In a network lobby, Enter is the host starting the match with whoever is there.
        if (scancode == Scancode.Return && _relay is { Started: false })
        {
            ExecuteGameCommand(GameCommand.StartNetMatch, InputCommandSources.Input);
            return;
        }

        // After a match ends, Enter starts a new one.
        if (scancode == Scancode.Return && CanRestartMatch)
        {
            ExecuteGameCommand(GameCommand.RestartMatch, InputCommandSources.Input);
            return;
        }

        if (GameCommandController.TryTranslateHotkey(scancode, shiftHeld, _commandController.ShowPostPassOverlay, out GameCommand command))
            ExecuteGameCommand(command, InputCommandSources.Input);
    }

    /// <summary>
    /// A match is running and has ended, so Enter (or a RestartMatch command) starts a new one. In a network match
    /// that is a new match in the same room: the relay has been told this one is over, so the first player to
    /// restart opens the room again and the others join it.
    /// </summary>
    private bool CanRestartMatch => !_terrainTestMode && (_world.IsMatchOver || CanRejoinMatch);

    /// <summary>
    /// The connection to a started network match is lost and the match is not over: Enter starts the game again,
    /// which comes back to the match under the same name and replays it up to the present.
    /// </summary>
    private bool CanRejoinMatch => _relay is { Started: true, Failure: not null, DesyncTick: null } && !_world.IsMatchOver;

    /// <summary>True when the restart that was asked for is a return to the running match, not a new match.</summary>
    public bool RejoinRequested { get; private set; }

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
        if (command == GameCommand.StartNetMatch)
        {
            // The relay ignores the request from anyone but the host.
            if (_relay is { Started: false, IsHost: true })
                _relay.RequestStart();
            return;
        }

        if (command == GameCommand.RestartMatch)
        {
            if (CanRestartMatch)
            {
                Console.WriteLine($"[Game] Restarting match [source={source}]");
                RejoinRequested = CanRejoinMatch;
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

    private DirectionF? ComputeAimDirection(IReadOnlyList<Tank> tanks, in FrameInputSnapshot frameInput)
    {
        if (LocalTank is not { } tank) return null;

        if (!IsMouseInViewport(frameInput, out float relX, out float relY))
            return null;

        int scale = _renderViewState.CellPixels;
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

        // The mouse is in points; the view is in device pixels.
        float density = _renderViewState.PixelDensity;
        relX = frameInput.MouseX * density;
        relY = frameInput.MouseY * density;
        if (relX < 0 || relY < 0 || relX >= vpW || relY >= vpH)
            return false;

        // The HUD's two parts are drawn over the bottom corners of the view; the mouse over them does not aim.
        return !_renderViewState.IsUnderHud(relX, relY);
    }
}
