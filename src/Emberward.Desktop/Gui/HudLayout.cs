namespace Emberward.Desktop.Gui;

using System.Numerics;
using Emberward.Desktop.Rendering;

/// <summary>
/// Size and placement of the bottom HUD: the art's own dimensions, how much it is enlarged for the display, and where
/// it sits on screen this frame. The strip is split in two so the middle of the view stays clear: the TEMP, SHIELD and
/// MATERIALS panels sit in the bottom-left corner and BUILD STRUCTURES in the bottom-right. Art coordinates still run
/// across the whole strip (0 to <see cref="StripW"/>); <see cref="At(float, float)"/> moves the right part over.
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

    /// <summary>
    /// Widths of the two parts, in art pixels: the left one ends where BUILD STRUCTURES begins. The art was painted as
    /// one strip, so the facing ends of MATERIALS and BUILD STRUCTURES are cut along their frames, with a bevel.
    /// </summary>
    public const float LeftW = BuildX, RightW = BuildPanelW;

    /// <summary>
    /// The narrowest clear gap between the two parts, in UI units (not scaled with the art), so the tank in the middle
    /// of the view and the ground around it stay clear. Displays too narrow for it shrink the HUD.
    /// </summary>
    public const float MinCenterGap = 404f;

    /// <summary>Where the left part's top-left corner is on screen this frame: the bottom-left corner of the display.</summary>
    public Vector2 Origin { get; private set; }

    /// <summary>How far right the right part is moved, in screen units, so it ends at the display's right edge.</summary>
    public float RightShift { get; private set; }

    /// <summary>Screen pixels per art pixel this frame.</summary>
    public float Scale { get; private set; } = 1f;

    /// <summary>
    /// How much the HUD art is enlarged, in UI units (<see cref="ImGuiPlatform.UiScale"/> does the rest): 1 up to a
    /// 1080-unit-high display, in proportion above it, and shrunk only when the display is too narrow for both parts
    /// and the <see cref="MinCenterGap"/> between them. A display narrower than twice that gap gives half its width to
    /// the gap, so the scale stays positive however narrow the window is.
    /// </summary>
    public static float HudScale(float displayW, float displayH)
    {
        float gap = MathF.Min(MinCenterGap, displayW * 0.5f);
        float fitWidth = MathF.Max(0f, displayW - gap) / (LeftW + RightW);
        return MathF.Min(fitWidth, MathF.Max(1f, displayH / ReferenceDisplayHeight));
    }

    /// <summary>
    /// Screen pixels per art pixel, in UI units. On a high-density display this is snapped down to a whole number of
    /// device pixels, so the art lands on whole pixels with no resampling blur. Below <see cref="ArtTextureDensity"/>
    /// device pixels per art pixel the textures are scaled down anyway, so snapping would only shrink the HUD (to half
    /// size on a 2x display narrow enough to scale it below 1): there, and on 1x displays, the fractional scale is kept.
    /// </summary>
    /// <param name="uiScale">Points per UI unit.</param>
    /// <param name="pixelDensity">Device pixels per point.</param>
    public static float ArtScale(float displayW, float displayH, float uiScale, float pixelDensity)
    {
        float scale = HudScale(displayW, displayH);
        if (pixelDensity <= 1f)
            return scale;

        float devicePerUnit = uiScale * pixelDensity;
        float exact = scale * devicePerUnit;
        return exact < ArtTextureDensity ? scale : MathF.Floor(exact) / devicePerUnit;
    }

    /// <summary>Texture pixels per art pixel: the HUD PNGs are drawn at twice their layout size.</summary>
    public const float ArtTextureDensity = 2f;

    /// <summary>Points at the bottom of the window covered by the HUD; the game view is drawn above them.</summary>
    /// <param name="windowW">Window width in points.</param>
    /// <param name="windowH">Window height in points.</param>
    /// <param name="pixelDensity">Device pixels per point.</param>
    public static int BottomPanelHeight(int windowW, int windowH, float pixelDensity)
    {
        float ui = ImGuiPlatform.ComputeUiScale(windowW, windowH);
        float scale = ArtScale(windowW / ui, windowH / ui, ui, pixelDensity);
        return (int)MathF.Ceiling(StripH * scale * ui);
    }

    /// <summary>
    /// Whether a point of the window is under the HUD, where the mouse works the HUD and does not aim: the two parts,
    /// each taken as a rectangle from the strip's top edge down. The gap between them is part of the view.
    /// </summary>
    /// <param name="windowW">Window width in points.</param>
    /// <param name="windowH">Window height in points.</param>
    /// <param name="pixelDensity">Device pixels per point.</param>
    /// <param name="x">Point x, in points.</param>
    /// <param name="y">Point y, in points.</param>
    public static bool Covers(int windowW, int windowH, float pixelDensity, float x, float y)
    {
        if (y < windowH - BottomPanelHeight(windowW, windowH, pixelDensity))
            return false;
        float ui = ImGuiPlatform.ComputeUiScale(windowW, windowH);
        float pointsPerArtPixel = ArtScale(windowW / ui, windowH / ui, ui, pixelDensity) * ui;
        return x < LeftW * pointsPerArtPixel || x >= windowW - RightW * pointsPerArtPixel;
    }

    /// <summary>Places the two parts in the bottom corners of a display of this size (in UI units).</summary>
    public void Update(Vector2 displaySize)
    {
        float ui = ImGuiPlatform.UiScale;
        float density = ImGuiPlatform.PixelDensity;
        Scale = ArtScale(displaySize.X, displaySize.Y, ui, density);
        int windowW = (int)MathF.Round(displaySize.X * ui), windowH = (int)MathF.Round(displaySize.Y * ui);
        Origin = new Vector2(0f, displaySize.Y - BottomPanelHeight(windowW, windowH, density) / ui);
        // Snapped to whole device pixels, like the art scale, so the right part is not resampled either.
        float deviceScale = ImGuiPlatform.DeviceScale;
        float rightX = MathF.Round((displaySize.X - RightW * Scale) * deviceScale) / deviceScale;
        RightShift = rightX - BuildX * Scale;
    }

    /// <summary>
    /// Art pixel to screen position, for the HUD drawn at the current scale. Points from <see cref="BuildX"/> on belong
    /// to the right part.
    /// </summary>
    public Vector2 At(float x, float y) =>
        Origin + new Vector2(x, y) * Scale + new Vector2(x >= BuildX ? RightShift : 0f, 0f);
    public Vector2 At(Vector2 p) => At(p.X, p.Y);

    /// <summary>An art length in screen pixels.</summary>
    public float S(float v) => v * Scale;
}
