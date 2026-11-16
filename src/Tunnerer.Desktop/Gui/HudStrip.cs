namespace Tunnerer.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Tunnerer.Core;
using Tunnerer.Core.Entities;
using Tunnerer.Desktop.Rendering;
using static Tunnerer.Desktop.Gui.HudLayout;

/// <summary>
/// The art panels along the bottom of the screen: TEMP, SHIELD and MATERIALS in the bottom-left corner, BUILD
/// STRUCTURES in the bottom-right (<see cref="HudLayout"/>), with their readouts and bars. The middle of the view is
/// left clear. The art carries no text: titles and numbers are drawn with the UI font, so they stay sharp at any
/// resolution.
/// </summary>
public sealed class HudStrip
{
    // The readout numbers' green, and the height of the line they sit in (art pixels).
    private const uint ReadoutColor = 0xFFAAFACDu;
    private const float ReadoutLineH = 20f;

    // Centre of each plate, in art pixels. The panel's title and its readout both centre on it, so they line up.
    private const float TempCenterX = HeatX + 168f;
    private const float ShieldCenterX = ShieldX + 80f;
    private const float MaterialsCenterX = MaterialsX + 120f;

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
        const float plateX = HeatX + 105f;
        const float plateW = 130f;
        const float plateY = 168f;
        int degrees = player.IsDead ? 0 : (int)MathF.Round(player.Heat);
        DrawNumberCentered(dl, degrees, TempCenterX, plateY, degreeSign: true);
        _heat.DrawTrend(dl, player, plateX + plateW - 14f, plateY + 10f);
        if (!player.IsDead)
            _heat.DrawBar(dl, player, plateX + 8f, 192f, plateW - 16f, time);
        _heat.DrawOffNetworkTag(dl, player, HeatX + HeatPanelW * 0.5f, time);

        // SHIELD: the tank's armor.
        int health = Math.Max(0, player.Reactor.Health);
        const float sPlateX = ShieldX + 10f;
        const float sPlateW = 125f;
        DrawNumberCentered(dl, health, ShieldCenterX, 166f);
        DrawBar(dl, sPlateX + 8f, 191f, sPlateW - 16f, 5f, player.ShieldFraction, player.ShieldLevel switch
        {
            StatusLevel.Ok => 0xFFFFC040u,
            StatusLevel.Warn => 0xFF30C0FFu,
            _ => 0xFF3040FFu,
        });

        // MATERIALS: the dirt in the tank's hold, which is what structures are built from; the bar is how full it is.
        float materialsFill = player.HoldFill;
        const float mPlateX = MaterialsX + 15f;
        const float mPlateW = MaterialsPanelW - 30f;
        DrawNumberCentered(dl, player.Resources.Dirt, MaterialsCenterX, 166f);
        DrawBar(dl, mPlateX + 40f, 191f, mPlateW - 80f, 5f, materialsFill, 0xFF40B8F0u);

        _build.Draw(dl, player, player.BuildableDirt(player.IsDockedAtOwnBase), BuildX, time);

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

    private void DrawBar(ImDrawListPtr dl, float x, float y, float w, float h, float fill, uint color)
    {
        fill = Math.Clamp(fill, 0f, 1f);
        dl.AddRectFilled(At(x - 1f, y - 1f), At(x + w + 1f, y + h + 1f), 0xC0000000u);
        dl.AddRectFilled(At(x, y), At(x + w * fill, y + h), color);
    }

    /// <summary>
    /// The panel titles, drawn on the plates of the art (which carries no text of its own). Positions are the plate
    /// centres in art pixels.
    /// </summary>
    private void DrawPanelLabels(ImDrawListPtr dl)
    {
        DrawStripText(dl, "TEMP", TempCenterX, 152f, 19f, 0xFF78C8FFu, glow: 0x181880FFu);
        DrawStripText(dl, "SHIELD", ShieldCenterX, 153.5f, 20f, 0xFFF8E890u, glow: 0x20FFA030u);
        DrawStripText(dl, "MATERIALS", MaterialsCenterX, 156.5f, 21f, 0xFF68E0FFu, glow: 0x282090FFu);
        DrawStripText(dl, "BUILD STRUCTURES", BuildX + 162f, 23.5f, 17f, 0xFFB0E4F8u, glow: 0x181880FFu);
        DrawStripText(dl, "COMMAND", BuildX + 171f, 191.5f, 21f, 0xFF98E8FFu, glow: 0x202080FFu);
    }

    /// <param name="degreeSign">Add a degree sign after the number.</param>
    private void DrawNumberCentered(ImDrawListPtr dl, int value, float centerX, float lineY, bool degreeSign = false)
    {
        string text = value.ToString(System.Globalization.CultureInfo.InvariantCulture) + (degreeSign ? "°" : "");
        DrawStripText(dl, text, centerX, lineY + ReadoutLineH * 0.5f, 24f, ReadoutColor, glow: 0x2830FF60u);
    }

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
        // Snap to whole device pixels so the glyphs are not resampled between texels.
        var pos = At(cx, cy) - extent * 0.5f - new Vector2(bold * 0.5f, 0f);
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
}
