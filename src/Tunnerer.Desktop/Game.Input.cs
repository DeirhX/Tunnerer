namespace Tunnerer.Desktop;

using Silk.NET.SDL;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Config;
using Tunnerer.Desktop.Input;
using Tunnerer.Desktop.Rendering;
using System.Runtime.InteropServices;

public partial class Game
{
    private void HandleEvent(Event ev)
    {
        if (TryMapRenderInputEvent(ev, out var renderEvent))
            _renderBackend.ProcessEvent(renderEvent);
        if (ev.Type != (uint)EventType.Keydown)
            return;

        // Ignore key-repeat so toggles flip once per physical press.
        if (ev.Key.Repeat != 0)
            return;

        Scancode scancode = (Scancode)ev.Key.Keysym.Scancode;
        bool shiftHeld = (ev.Key.Keysym.Mod & (ushort)Keymod.Shift) != 0;
        if (GameCommandController.TryTranslateHotkey(scancode, shiftHeld, out GameCommand command))
            ExecuteGameCommand(command, InputCommandSources.Input);
    }

    private static unsafe bool TryMapRenderInputEvent(Event ev, out RenderInputEvent renderEvent)
    {
        renderEvent = default;
        switch ((EventType)ev.Type)
        {
            case EventType.Mousemotion:
                renderEvent = new RenderInputEvent(
                    Type: RenderInputEventType.MouseMove,
                    MouseX: ev.Motion.X,
                    MouseY: ev.Motion.Y);
                return true;
            case EventType.Mousebuttondown:
            case EventType.Mousebuttonup:
                renderEvent = new RenderInputEvent(
                    Type: RenderInputEventType.MouseButton,
                    MouseButton: ev.Button.Button switch
                    {
                        1 => RenderMouseButton.Left,
                        2 => RenderMouseButton.Middle,
                        3 => RenderMouseButton.Right,
                        _ => RenderMouseButton.Unknown,
                    },
                    IsPressed: ev.Type == (uint)EventType.Mousebuttondown);
                return true;
            case EventType.Mousewheel:
                renderEvent = new RenderInputEvent(
                    Type: RenderInputEventType.MouseWheel,
                    WheelX: ev.Wheel.X,
                    WheelY: ev.Wheel.Y);
                return true;
            case EventType.Textinput:
                renderEvent = new RenderInputEvent(
                    Type: RenderInputEventType.TextInput,
                    Text: Marshal.PtrToStringUTF8((nint)ev.Text.Text));
                return true;
            case EventType.Keydown:
            case EventType.Keyup:
                renderEvent = new RenderInputEvent(
                    Type: RenderInputEventType.Key,
                    Key: ToRenderKey((Scancode)ev.Key.Keysym.Scancode),
                    Modifiers: ToRenderModifiers(ev.Key.Keysym.Mod),
                    IsPressed: ev.Type == (uint)EventType.Keydown,
                    IsRepeat: ev.Key.Repeat != 0);
                return true;
            default:
                return false;
        }
    }

    private static RenderKey ToRenderKey(Scancode scancode) => scancode switch
    {
        Scancode.ScancodeTab => RenderKey.Tab,
        Scancode.ScancodeLeft => RenderKey.LeftArrow,
        Scancode.ScancodeRight => RenderKey.RightArrow,
        Scancode.ScancodeUp => RenderKey.UpArrow,
        Scancode.ScancodeDown => RenderKey.DownArrow,
        Scancode.ScancodePageup => RenderKey.PageUp,
        Scancode.ScancodePagedown => RenderKey.PageDown,
        Scancode.ScancodeHome => RenderKey.Home,
        Scancode.ScancodeEnd => RenderKey.End,
        Scancode.ScancodeInsert => RenderKey.Insert,
        Scancode.ScancodeDelete => RenderKey.Delete,
        Scancode.ScancodeBackspace => RenderKey.Backspace,
        Scancode.ScancodeSpace => RenderKey.Space,
        Scancode.ScancodeReturn => RenderKey.Enter,
        Scancode.ScancodeEscape => RenderKey.Escape,
        Scancode.ScancodeA => RenderKey.A,
        Scancode.ScancodeC => RenderKey.C,
        Scancode.ScancodeV => RenderKey.V,
        Scancode.ScancodeX => RenderKey.X,
        Scancode.ScancodeY => RenderKey.Y,
        Scancode.ScancodeZ => RenderKey.Z,
        _ => RenderKey.None,
    };

    private static RenderKeyModifiers ToRenderModifiers(ushort modifiers)
    {
        RenderKeyModifiers mapped = RenderKeyModifiers.None;
        if ((modifiers & (ushort)Keymod.Ctrl) != 0)
            mapped |= RenderKeyModifiers.Ctrl;
        if ((modifiers & (ushort)Keymod.Shift) != 0)
            mapped |= RenderKeyModifiers.Shift;
        if ((modifiers & (ushort)Keymod.Alt) != 0)
            mapped |= RenderKeyModifiers.Alt;
        return mapped;
    }

    private void ExecuteGameCommand(GameCommand command, string source)
    {
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
