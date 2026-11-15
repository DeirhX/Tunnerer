namespace Tunnerer.Desktop.Gui;

using System.Numerics;

/// <summary>
/// Size and placement of the bottom HUD strip: the art's own dimensions, how much it is enlarged for the display, and
/// where it sits on screen this frame. Art pixel (x, y) is drawn at <see cref="Origin"/> + (x, y) * <see cref="Scale"/>.
/// </summary>
public sealed class HudLayout
{
    /// <summary>Height of the HUD art in its own pixels; on screen it is multiplied by the HUD scale.</summary>
    public const float StripH = 223f;

    /// <summary>The display height at which the HUD art is drawn 1:1; taller displays enlarge it.</summary>
    public const float ReferenceDisplayHeight = 1080f;

    // Layout of the bottom strip (ImGui units): the four art panels drawn side by side.
    public const float HeatPanelW = 245f, ShieldPanelW = 145f, MaterialsPanelW = 230f, BuildPanelW = 404f;
    public const float StripW = HeatPanelW + ShieldPanelW + MaterialsPanelW + BuildPanelW; // 1024

    public const float HeatX = 0f;
    public const float ShieldX = HeatX + HeatPanelW;
    public const float MaterialsX = ShieldX + ShieldPanelW;
    public const float BuildX = MaterialsX + MaterialsPanelW;

    /// <summary>Where the strip's top-left corner is on screen this frame.</summary>
    public Vector2 Origin { get; private set; }

    /// <summary>Screen pixels per art pixel this frame.</summary>
    public float Scale { get; private set; } = 1f;

    /// <summary>
    /// How much the HUD art is enlarged: 1 up to a 1080-pixel-high display, in proportion above it (2 at 4K), and
    /// shrunk only when the display is narrower than the art.
    /// </summary>
    public static float HudScale(float displayW, float displayH) =>
        MathF.Min(displayW / StripW, MathF.Max(1f, displayH / ReferenceDisplayHeight));

    /// <summary>Screen pixels at the bottom of the window covered by the HUD; the game view is drawn above them.</summary>
    public static int BottomPanelHeight(int windowW, int windowH) =>
        (int)MathF.Ceiling(StripH * HudScale(windowW, windowH));

    /// <summary>Centres the strip at the bottom of a display of this size.</summary>
    public void Update(Vector2 displaySize)
    {
        Scale = HudScale(displaySize.X, displaySize.Y);
        float stripScreenW = StripW * Scale;
        Origin = new Vector2(
            MathF.Floor((displaySize.X - stripScreenW) * 0.5f),
            displaySize.Y - BottomPanelHeight((int)displaySize.X, (int)displaySize.Y));
    }

    /// <summary>Art pixel to screen position, for the HUD drawn at the current scale.</summary>
    public Vector2 At(float x, float y) => Origin + new Vector2(x, y) * Scale;
    public Vector2 At(Vector2 p) => Origin + p * Scale;

    /// <summary>An art length in screen pixels.</summary>
    public float S(float v) => v * Scale;
}
