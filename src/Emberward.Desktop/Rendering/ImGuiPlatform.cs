namespace Emberward.Desktop.Rendering;

using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

/// <summary>
/// The backend-neutral half of the Dear ImGui integration: the context, input events and per-frame setup. Each
/// render backend adds only the part that draws <see cref="ImGui.GetDrawData"/>.
/// </summary>
public static class ImGuiPlatform
{
    /// <param name="windowW">Window width in points.</param>
    /// <param name="windowH">Window height in points.</param>
    /// <param name="pixelDensity">Device pixels per point (2 on Retina).</param>
    public static void CreateContext(int windowW, int windowH, float pixelDensity)
    {
        var imguiContext = ImGui.CreateContext();
        ImGui.SetCurrentContext(imguiContext);

        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        io.BackendFlags |= ImGuiBackendFlags.HasMouseCursors;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        ApplyUiScale(io, windowW, windowH, pixelDensity);

        ImGui.StyleColorsDark();
        LoadUiFont(io);
    }

    /// <summary>Window height the UI is laid out for; larger windows draw it proportionally bigger.</summary>
    public const float ReferenceHeight = 1080f;
    private const float ReferenceWidth = 1920f;

    /// <summary>
    /// Window points per UI unit: 1 up to 1920×1080, in proportion above it (2 at 4K). The whole UI is laid out in
    /// UI units (<c>DisplaySize</c> is the window in points divided by this) and drawn scaled up, so every panel, text
    /// and menu keeps its size relative to the screen.
    /// </summary>
    public static float UiScale { get; private set; } = 1f;

    /// <summary>
    /// Device pixels per point: 2 on a Retina display, 1 on a standard one. The UI is drawn at this density, so text and
    /// art land on device pixels instead of being stretched by the OS.
    /// </summary>
    public static float PixelDensity { get; private set; } = 1f;

    /// <summary>
    /// Device pixels per UI unit. The game view and everything measured in it (camera, cells, the game texture) is in
    /// device pixels; divide by this to get UI units.
    /// </summary>
    public static float DeviceScale => UiScale * PixelDensity;

    /// <param name="windowW">Window width in points.</param>
    /// <param name="windowH">Window height in points.</param>
    public static float ComputeUiScale(int windowW, int windowH) =>
        MathF.Max(1f, MathF.Min(windowH / ReferenceHeight, windowW / ReferenceWidth));

    private static void ApplyUiScale(ImGuiIOPtr io, int windowW, int windowH, float pixelDensity)
    {
        UiScale = ComputeUiScale(windowW, windowH);
        PixelDensity = pixelDensity > 0f ? pixelDensity : 1f;
        io.DisplaySize = new Vector2(windowW, windowH) / UiScale;
        io.DisplayFramebufferScale = new Vector2(DeviceScale);
    }

    /// <summary>
    /// The UI font (Chakra Petch, SIL OFL) used unless <c>EMBERWARD_UI_FONT</c> names another file in
    /// <c>resources/fonts</c>; setting it to "default" keeps ImGui's built-in font.
    /// </summary>
    public const string DefaultUiFont = "ChakraPetch-Medium.ttf";

    // Baked large and scaled down for plain widget text, so the bigger sizes the HUD draws stay sharp.
    private const float UiFontBakeSize = 32f;
    private const float UiFontWidgetSize = 16f;

    /// <summary>
    /// Loads the UI font: the one named by <c>EMBERWARD_UI_FONT</c>, else <see cref="DefaultUiFont"/>. The font the
    /// executable carries is used, unless <c>resources/fonts</c> beside the executable has a file of that name, which
    /// lets a developer try another font without a rebuild. Falls back to ImGui's built-in font when neither has it.
    /// </summary>
    private static unsafe void LoadUiFont(ImGuiIOPtr io)
    {
        string name = Environment.GetEnvironmentVariable("EMBERWARD_UI_FONT") is { Length: > 0 } chosen ? chosen : DefaultUiFont;
        if (string.Equals(name, "default", StringComparison.OrdinalIgnoreCase))
            return;
        byte[]? data = ReadUiFontData(name);
        if (data is null)
        {
            Console.WriteLine($"[UI] Font '{name}' is neither embedded nor in resources/fonts; using the built-in font.");
            return;
        }

        // ImGui reads the font data for as long as the atlas lives, so the bytes stay pinned for the whole run.
        if (_fontDataHandle.IsAllocated)
            _fontDataHandle.Free();
        _fontDataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
        nint fontData = _fontDataHandle.AddrOfPinnedObject();

        var config = ImGuiNative.ImFontConfig_ImFontConfig();
        config->FontDataOwnedByAtlas = 0; // the bytes above belong to this program, not to the atlas
        config->OversampleH = 3;
        config->OversampleV = 2;
        config->PixelSnapH = 0;
        io.Fonts.AddFontFromMemoryTTF(fontData, data.Length, UiFontBakeSize, config, io.Fonts.GetGlyphRangesDefault());
        config->OversampleH = 2;
        config->OversampleV = 1;
        LargeFont = io.Fonts.AddFontFromMemoryTTF(fontData, data.Length, LargeFontBakeSize, config, io.Fonts.GetGlyphRangesDefault());
        ImGuiNative.ImFontConfig_destroy(config);
        io.FontGlobalScale = UiFontWidgetSize / UiFontBakeSize;
        Console.WriteLine($"[UI] Font {name}");
    }

    private static GCHandle _fontDataHandle;

    /// <summary>The bytes of a UI font: a file in <c>resources/fonts</c> beside the executable, else the embedded one.</summary>
    private static byte[]? ReadUiFontData(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "resources", "fonts", name);
        if (File.Exists(path))
            return File.ReadAllBytes(path);
        string asset = "resources/fonts/" + name;
        return EmbeddedAssets.Exists(asset) ? EmbeddedAssets.ReadBytes(asset) : null;
    }

    private const float LargeFontBakeSize = 72f;

    /// <summary>
    /// The UI font baked again at a large size, for text drawn far above the regular bake (the HUD strip's labels and
    /// numbers at 4K). Null with ImGui's built-in font.
    /// </summary>
    public static ImFontPtr? LargeFont { get; private set; }

    /// <summary>The font that stays sharp for text <paramref name="windowPixels"/> high on screen.</summary>
    public static ImFontPtr FontFor(float windowPixels) =>
        LargeFont is { } large && windowPixels > UiFontBakeSize * 1.1f ? large : ImGui.GetFont();

    public static void DestroyContext() => ImGui.DestroyContext();

    public static void ProcessEvent(in RenderInputEvent ev)
    {
        var io = ImGui.GetIO();
        switch (ev.Type)
        {
            case RenderInputEventType.MouseMove:
                io.AddMousePosEvent(ev.MouseX / UiScale, ev.MouseY / UiScale);
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

    /// <param name="windowW">Window width in points.</param>
    /// <param name="windowH">Window height in points.</param>
    /// <param name="pixelDensity">Device pixels per point (2 on Retina).</param>
    public static void NewFrame(int windowW, int windowH, float pixelDensity, float deltaTime)
    {
        var io = ImGui.GetIO();
        ApplyUiScale(io, windowW, windowH, pixelDensity);
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
