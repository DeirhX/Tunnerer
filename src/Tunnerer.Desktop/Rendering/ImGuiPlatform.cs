namespace Tunnerer.Desktop.Rendering;

using System.Numerics;
using ImGuiNET;

/// <summary>
/// The backend-neutral half of the Dear ImGui integration: the context, input events and per-frame setup. Each
/// render backend adds only the part that draws <see cref="ImGui.GetDrawData"/>.
/// </summary>
public static class ImGuiPlatform
{
    public static void CreateContext(int windowW, int windowH)
    {
        var imguiContext = ImGui.CreateContext();
        ImGui.SetCurrentContext(imguiContext);

        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        io.BackendFlags |= ImGuiBackendFlags.HasMouseCursors;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        io.DisplaySize = new Vector2(windowW, windowH);
        io.DisplayFramebufferScale = Vector2.One;

        ImGui.StyleColorsDark();
    }

    public static void DestroyContext() => ImGui.DestroyContext();

    public static void ProcessEvent(in RenderInputEvent ev)
    {
        var io = ImGui.GetIO();
        switch (ev.Type)
        {
            case RenderInputEventType.MouseMove:
                io.AddMousePosEvent(ev.MouseX, ev.MouseY);
                break;
            case RenderInputEventType.MouseButton:
            {
                int button = ev.MouseButton switch
                {
                    RenderMouseButton.Left => 0,
                    RenderMouseButton.Middle => 2,
                    RenderMouseButton.Right => 1,
                    _ => -1
                };
                if (button >= 0)
                    io.AddMouseButtonEvent(button, ev.IsPressed);
                break;
            }
            case RenderInputEventType.MouseWheel:
                io.AddMouseWheelEvent(ev.WheelX, ev.WheelY);
                break;
            case RenderInputEventType.TextInput:
            {
                string text = ev.Text ?? string.Empty;
                foreach (char c in text)
                    io.AddInputCharacter(c);
                break;
            }
            case RenderInputEventType.Key:
            {
                var key = ToImGuiKey(ev.Key);
                if (key != ImGuiKey.None)
                    io.AddKeyEvent(key, ev.IsPressed);

                io.AddKeyEvent(ImGuiKey.ModCtrl, (ev.Modifiers & RenderKeyModifiers.Ctrl) != 0);
                io.AddKeyEvent(ImGuiKey.ModShift, (ev.Modifiers & RenderKeyModifiers.Shift) != 0);
                io.AddKeyEvent(ImGuiKey.ModAlt, (ev.Modifiers & RenderKeyModifiers.Alt) != 0);
                break;
            }
        }
    }

    public static void NewFrame(int windowW, int windowH, float deltaTime)
    {
        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(windowW, windowH);
        io.DisplayFramebufferScale = Vector2.One;
        io.DeltaTime = deltaTime > 0 ? deltaTime : 1f / 60f;
        ImGui.NewFrame();
    }

    private static ImGuiKey ToImGuiKey(RenderKey key) => key switch
    {
        RenderKey.Tab => ImGuiKey.Tab,
        RenderKey.LeftArrow => ImGuiKey.LeftArrow,
        RenderKey.RightArrow => ImGuiKey.RightArrow,
        RenderKey.UpArrow => ImGuiKey.UpArrow,
        RenderKey.DownArrow => ImGuiKey.DownArrow,
        RenderKey.PageUp => ImGuiKey.PageUp,
        RenderKey.PageDown => ImGuiKey.PageDown,
        RenderKey.Home => ImGuiKey.Home,
        RenderKey.End => ImGuiKey.End,
        RenderKey.Insert => ImGuiKey.Insert,
        RenderKey.Delete => ImGuiKey.Delete,
        RenderKey.Backspace => ImGuiKey.Backspace,
        RenderKey.Space => ImGuiKey.Space,
        RenderKey.Enter => ImGuiKey.Enter,
        RenderKey.Escape => ImGuiKey.Escape,
        RenderKey.A => ImGuiKey.A,
        RenderKey.C => ImGuiKey.C,
        RenderKey.V => ImGuiKey.V,
        RenderKey.X => ImGuiKey.X,
        RenderKey.Y => ImGuiKey.Y,
        RenderKey.Z => ImGuiKey.Z,
        _ => ImGuiKey.None
    };
}
