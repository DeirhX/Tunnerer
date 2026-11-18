namespace Tunnerer.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Tunnerer.Core.Entities;
using Tunnerer.Desktop.Rendering;
using Tunnerer.Core.Entities.Tanks;
using static Tunnerer.Desktop.Gui.HudLayout;

/// <summary>
/// The art panels along the bottom of the screen: TEMP, SHIELD and MATERIALS in the bottom-left corner, BUILD
/// STRUCTURES in the bottom-right (<see cref="HudLayout"/>), with their readouts and bars. The middle of the view is
/// left clear. The art carries no text: titles and numbers are drawn with the UI font, so they stay sharp at any
/// resolution.
/// </summary>
public sealed class HudStrip
{
    // The readout numbers' green and size (art pixels).
    private const uint ReadoutColor = 0xFFAAFACDu;
    private const float ReadoutSize = 22f;

    // The ore line, drawn in the chest window above the MATERIALS plates (art pixels): teal like the ore, amber once
    // the hold is full, bright green while it unloads.
    private const float OreLineX = MaterialsX + 126f, OreLineY = 84f, OreLineSize = 19f;
    private const uint OreLineColor = 0xFFD8E860u, OreLineFullColor = 0xFF40C0FFu, OreLineUnloadColor = 0xFF60FF90u;

    /// <summary>
    /// A panel's plates, measured on the art (art pixels): the centre both lines share, the title plate's centre, and
    /// the readout plate's inner edges. The readout is centred in its plate. The levels themselves show in the art
    /// above (the heat orb, the shield and the chest fill), so the plates carry no bars.
    /// </summary>
    private readonly record struct Plate(float CenterX, float TitleY, float ReadoutLeft, float ReadoutTop, float ReadoutRight, float ReadoutBottom)
    {
        public float ReadoutY => (ReadoutTop + ReadoutBottom) * 0.5f;
    }

    private static readonly Plate TempPlate = new(HeatX + 170f, 151.5f, HeatX + 101f, 168f, HeatX + 239f, 190f);
    private static readonly Plate ShieldPlate = new(ShieldX + 80.5f, 153.5f, ShieldX + 11f, 169f, ShieldX + 148f, 191f);
    private static readonly Plate MaterialsPlate = new(MaterialsX + 120f, 154f, MaterialsX + 27f, 174f, MaterialsX + 213f, 194f);

    private readonly HudLayout _layout = new();
    private readonly HeatGauge _heat;
    private readonly LiquidGauges _liquids;
    private readonly BuildPanel _build;

    private nint _heatIconTex, _shieldIconTex;
    private nint _panelFrameTex, _buildPanelTex;

    public HudStrip()
    {
        _heat = new HeatGauge(_layout);
        _liquids = new LiquidGauges(_layout);
        _build = new BuildPanel(_layout);
    }

    /// <summary>State of the temperature orb shader for the frame just drawn; the game passes it to the backend.</summary>
    public HeatOrbState HeatOrb => _heat.HeatOrb;

    /// <summary>Screen y of the strip's top edge, as of the last frame drawn.</summary>
    public float Top => _layout.Origin.Y;

    private Vector2 At(float x, float y) => _layout.At(x, y);
    private float S(float v) => _layout.S(v);

    public void Init(nint heatIcon, nint shieldIcon, nint panelFrame, nint buildPanel)
    {
        _heatIconTex = heatIcon;
        _shieldIconTex = shieldIcon;
        _panelFrameTex = panelFrame;
        _buildPanelTex = buildPanel;
    }

    public void Draw(Tank player, float time)
    {
        var displaySize = ImGui.GetIO().DisplaySize;
        var dl = ImGui.GetForegroundDrawList();

        _layout.Update(displaySize);

        DrawPanelArt(dl, _heatIconTex, HeatX, HeatPanelW);
        DrawPanelArt(dl, _shieldIconTex, ShieldX, ShieldPanelW);
        DrawPanelArt(dl, _panelFrameTex, MaterialsX, MaterialsPanelW);
        DrawPanelArt(dl, _buildPanelTex, BuildX, BuildPanelW);
        DrawPanelLabels(dl);

        _heat.DrawOrb(dl, player, time);
        _liquids.Draw(dl, player, ShieldX, MaterialsX, time);

        // TEMP: the tank's temperature in degrees above ambient. Digging and firing heat it; past the safe maximum
        // the heat burns the shield. A tank docked at its own base is pre-cooled below ambient (negative degrees).
        int degrees = player.IsDead ? 0 : (int)MathF.Round(player.Heat);
        DrawReadout(dl, TempPlate, degrees.ToString(System.Globalization.CultureInfo.InvariantCulture) + "°");
        _heat.DrawTrend(dl, player, TempPlate.ReadoutRight - 12f, TempPlate.ReadoutY);
        _heat.DrawOffNetworkTag(dl, player, HeatX + HeatPanelW * 0.5f, time);

        // SHIELD: the tank's armor.
        int health = Math.Max(0, player.Reactor.Health);
        DrawReadout(dl, ShieldPlate, health.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // MATERIALS: the ore in the tank's hold against what it takes, the currency structures are built from.
        DrawReadout(dl, MaterialsPlate, string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{player.OreHeld} / {player.OreHoldCapacity}"));
        // The chest line: the hold emptying into the base (docked, or through the network), else the base store.
        string oreLine = BuildSlotView.OreLine(player);
        DrawStripText(dl, oreLine, OreLineX, OreLineY, OreLineSize,
            player.IsUnloadingOre ? OreLineUnloadColor : player.IsOreHoldFull ? OreLineFullColor : OreLineColor,
            glow: 0x28806020u);

        if (player.IsUnloadingOre)
        {
            // Down arrows either side of the word, bobbing: the ore is going down into the base.
            float bob = (time * 3f) % 1f * 5f;
            foreach (float dx in (ReadOnlySpan<float>)[-72f, 72f])
            {
                float cx = OreLineX + dx, top = OreLineY - 8f + bob;
                dl.AddTriangleFilled(At(cx - 7f, top), At(cx + 7f, top), At(cx, top + 9f), OreLineUnloadColor);
            }
        }

        _build.Draw(dl, player, BuildX, time);

        // The COMMAND button stands for the base upgrades: lit while docked (keys 1-4 buy), dimmed elsewhere.
        if (!player.IsDockedAtOwnBase)
            dl.AddRectFilled(At(BuildX + 77f, 172f), At(BuildX + 277f, 212f), 0x90000000u, S(10f));
        else
            dl.AddRect(At(BuildX + 77f, 172f), At(BuildX + 277f, 212f),
                (int)(time * 2f) % 2 == 0 ? 0xFF40FF60u : 0xFF208030u, S(10f), ImDrawFlags.None, S(2.5f));
    }

    /// <summary>One panel's art, placed by its left edge and sized from its width (its right edge may be where the
    /// other part of the HUD begins, which <see cref="HudLayout.At(float, float)"/> would move).</summary>
    private void DrawPanelArt(ImDrawListPtr dl, nint texture, float x, float w)
    {
        var min = At(x, 0f);
        dl.AddImage(texture, min, min + new Vector2(S(w), S(StripH)));
    }

    /// <summary>
    /// The panel titles, drawn on the plates of the art (which carries no text of its own). Positions are the plate
    /// centres in art pixels.
    /// </summary>
    private void DrawPanelLabels(ImDrawListPtr dl)
    {
        DrawStripText(dl, "TEMP", TempPlate.CenterX, TempPlate.TitleY, 19f, 0xFF78C8FFu, glow: 0x181880FFu);
        DrawStripText(dl, "SHIELD", ShieldPlate.CenterX, ShieldPlate.TitleY, 20f, 0xFFF8E890u, glow: 0x20FFA030u);
        DrawStripText(dl, "MATERIALS", MaterialsPlate.CenterX, MaterialsPlate.TitleY, 21f, 0xFF68E0FFu, glow: 0x282090FFu);
        DrawStripText(dl, "BUILD STRUCTURES", BuildX + 162f, 23.5f, 17f, 0xFFB0E4F8u, glow: 0x181880FFu);
        DrawStripText(dl, "COMMAND", BuildX + 171f, 191.5f, 21f, 0xFF98E8FFu, glow: 0x202080FFu);
    }

    /// <summary>A plate's number, centred in its readout plate above the bar.</summary>
    private void DrawReadout(ImDrawListPtr dl, Plate plate, string text) =>
        DrawStripText(dl, text, plate.CenterX, plate.ReadoutY, ReadoutSize, ReadoutColor, glow: 0x2830FF60u);

    /// <summary>
    /// Text in the strip, centred on art pixel (<paramref name="cx"/>, <paramref name="cy"/>) and sized in art pixels,
    /// so it scales with the art; drawn from the large font bake when big on screen, so it stays sharp at 4K. A dark
    /// outline keeps it legible on the plates, and an optional soft <paramref name="glow"/> lights it like the art.
    /// </summary>
    private void DrawStripText(ImDrawListPtr dl, string text, float cx, float cy, float size, uint color, uint glow = 0)
    {
        float px = S(size);
        float deviceScale = ImGuiPlatform.DeviceScale;
        var font = ImGuiPlatform.FontFor(px * deviceScale);
        var extent = font.CalcTextSizeA(px, float.MaxValue, 0f, text);
        // The bold pass is drawn bold px to the right, so start half of that to the left to keep the ink centred.
        float bold = S(0.6f);
        // Centre the ink, not the line box: capitals and digits sit between the top and bottom of '0', which is
        // not the middle of the line (that includes room for descenders).
        float inkMidY = InkMiddle(font) * px;
        // Snap to whole device pixels so the glyphs are not resampled between texels.
        var pos = At(cx, cy) - new Vector2(extent.X * 0.5f + bold * 0.5f, inkMidY);
        pos = new Vector2(MathF.Round(pos.X * deviceScale) / deviceScale, MathF.Round(pos.Y * deviceScale) / deviceScale);

        if (glow != 0)
        {
            float r = S(3f);
            for (int i = 0; i < 12; i++)
            {
                float a = i * MathF.PI / 6f;
                dl.AddText(font, px, pos + new Vector2(MathF.Cos(a), MathF.Sin(a) * 0.7f) * r, glow, text);
            }
        }
        // Heavier than the font's own weight, to match the art's chunky lettering: each pass is drawn twice, a
        // fraction of a pixel apart.
        float o = MathF.Max(1f / deviceScale, S(1f));
        const uint outline = 0xE0000000u;
        foreach (var d in (ReadOnlySpan<Vector2>)[new(-o, 0f), new(o + bold, 0f), new(0f, -o), new(bold, -o), new(0f, o * 1.6f), new(bold, o * 1.6f)])
            dl.AddText(font, px, pos + d, outline, text);
        dl.AddText(font, px, pos, color, text);
        dl.AddText(font, px, pos + new Vector2(bold, 0f), color, text);
    }

    /// <summary>
    /// Where the middle of a capital or digit sits below the top of the line, as a fraction of the font size: the
    /// centre of the '0' glyph's box.
    /// </summary>
    private static float InkMiddle(ImFontPtr font)
    {
        // FindGlyph falls back to the font's fallback glyph, so it always returns one.
        var glyph = font.FindGlyph('0');
        return (glyph.Y0 + glyph.Y1) * 0.5f / font.FontSize;
    }
}
