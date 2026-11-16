namespace Tunnerer.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Tunnerer.Core.Entities;
using Tunnerer.Desktop.Rendering;
using static Tunnerer.Desktop.Gui.HudLayout;

/// <summary>
/// The art strip along the bottom of the screen: TEMP, SHIELD, MATERIALS and BUILD STRUCTURES panels, their digit
/// readouts and bars, extended to both screen edges.
/// </summary>
public sealed class HudStrip
{
    private const int DigitCount = 10;
    private const int DigitCellW = 17;
    private const int DigitCellH = 20;
    private const int DigitStripW = DigitCellW * DigitCount; // 170
    private const float DigitSpacing = 1f;

    // The strip is extended to both screen edges by repeating, mirrored, the chassis at its outer ends: the left
    // end of heat_icon.png and the right end of build_panel.png, from ExtensionTopY down. Mirroring every other
    // copy makes the joins seamless.
    private const float ExtensionTileW = 60f;
    private const float ExtensionTopY = 100f;

    private readonly HudLayout _layout = new();
    private readonly HeatGauge _heat;
    private readonly LiquidGauges _liquids;
    private readonly BuildPanel _build;

    private nint _heatIconTex, _shieldIconTex;
    private nint _panelFrameTex, _buildPanelTex;
    private nint _digitStripTex;

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

    public void Init(nint heatIcon, nint shieldIcon, nint panelFrame, nint buildPanel, nint digitStrip)
    {
        _heatIconTex = heatIcon;
        _shieldIconTex = shieldIcon;
        _panelFrameTex = panelFrame;
        _buildPanelTex = buildPanel;
        _digitStripTex = digitStrip;
    }

    public void Draw(Tank player, float time)
    {
        var displaySize = ImGui.GetIO().DisplaySize;
        var dl = ImGui.GetForegroundDrawList();

        _layout.Update(displaySize);

        DrawStripExtensions(dl, displaySize.X);

        const float h = StripH;
        dl.AddImage(_heatIconTex, At(HeatX, 0f), At(HeatX + HeatPanelW, h));
        dl.AddImage(_shieldIconTex, At(ShieldX, 0f), At(ShieldX + ShieldPanelW, h));
        dl.AddImage(_panelFrameTex, At(MaterialsX, 0f), At(MaterialsX + MaterialsPanelW, h));
        dl.AddImage(_buildPanelTex, At(BuildX, 0f), At(BuildX + BuildPanelW, h));

        _heat.DrawOrb(dl, player, time);
        _liquids.Draw(dl, player, ShieldX, MaterialsX, time);

        // TEMP: the tank's temperature in degrees above ambient. Digging and firing heat it; past the safe maximum
        // the heat burns the shield. A tank docked at its own base is pre-cooled below ambient (negative degrees).
        const float plateX = HeatX + 105f;
        const float plateW = 130f;
        const float plateY = 168f;
        int degrees = player.IsDead ? 0 : (int)MathF.Round(player.Heat);
        DrawNumberCentered(dl, degrees, plateX, plateY, plateW, degreeSign: true);
        _heat.DrawTrend(dl, player, plateX + plateW - 14f, plateY + 10f);
        if (!player.IsDead)
            _heat.DrawBar(dl, player.Heat, plateX + 8f, 192f, plateW - 16f, time);
        _heat.DrawOffNetworkTag(dl, player, HeatX + HeatPanelW * 0.5f, time);

        // SHIELD: the tank's armor.
        int health = Math.Max(0, player.Reactor.Health);
        const float sPlateX = ShieldX + 10f;
        const float sPlateW = 125f;
        DrawNumberCentered(dl, health, sPlateX, 166f, sPlateW);
        float shieldFill = player.Reactor.HealthCapacity > 0 ? health / (float)player.Reactor.HealthCapacity : 0f;
        DrawBar(dl, sPlateX + 8f, 191f, sPlateW - 16f, 5f, shieldFill,
            shieldFill > 0.5f ? 0xFFFFC040u : shieldFill > 0.25f ? 0xFF30C0FFu : 0xFF3040FFu);

        // MATERIALS: the dirt in the tank's hold, which is what structures are built from; the bar is how full it is.
        float materialsFill = LiquidGauges.MaterialsFill(player);
        const float mPlateX = MaterialsX + 15f;
        const float mPlateW = MaterialsPanelW - 30f;
        DrawNumberCentered(dl, player.Resources.Dirt, mPlateX, 166f, mPlateW);
        DrawBar(dl, mPlateX + 40f, 191f, mPlateW - 80f, 5f, materialsFill, 0xFF40B8F0u);

        _build.Draw(dl, player, player.BuildableDirt(player.IsDockedAtOwnBase), BuildX, time);

        // The COMMAND button stands for the base upgrades: lit while docked (keys 1-4 buy), dimmed elsewhere.
        if (!player.IsDockedAtOwnBase)
            dl.AddRectFilled(At(BuildX + 77f, 172f), At(BuildX + 277f, 212f), 0x90000000u, S(10f));
        else
            dl.AddRect(At(BuildX + 77f, 172f), At(BuildX + 277f, 212f),
                (int)(time * 2f) % 2 == 0 ? 0xFF40FF60u : 0xFF208030u, S(10f), ImDrawFlags.None, S(2.5f));
    }

    /// <summary>
    /// Fills the screen width on both sides of the strip with mirrored copies of the chassis at its outer ends, so
    /// the HUD runs from edge to edge at any aspect ratio.
    /// </summary>
    private void DrawStripExtensions(ImDrawListPtr dl, float displayW)
    {
        float tileW = S(ExtensionTileW);
        float top = At(0f, ExtensionTopY).Y;
        float bottom = At(0f, StripH).Y;
        float vTop = ExtensionTopY / StripH;

        // Left: the first ExtensionTileW columns of the heat panel, the copy touching the strip mirrored.
        float uHeat = ExtensionTileW / HeatPanelW;
        float x = _layout.Origin.X;
        for (int i = 0; x > 0f; i++, x -= tileW)
        {
            bool mirrored = i % 2 == 0;
            dl.AddImage(_heatIconTex, new Vector2(x - tileW, top), new Vector2(x, bottom),
                new Vector2(mirrored ? uHeat : 0f, vTop), new Vector2(mirrored ? 0f : uHeat, 1f));
        }

        // Right: the last columns of the build panel.
        float uBuild = 1f - ExtensionTileW / BuildPanelW;
        x = _layout.Origin.X + S(StripW);
        for (int i = 0; x < displayW; i++, x += tileW)
        {
            bool mirrored = i % 2 == 0;
            dl.AddImage(_buildPanelTex, new Vector2(x, top), new Vector2(x + tileW, bottom),
                new Vector2(mirrored ? 1f : uBuild, vTop), new Vector2(mirrored ? uBuild : 1f, 1f));
        }
    }

    private void DrawBar(ImDrawListPtr dl, float x, float y, float w, float h, float fill, uint color)
    {
        fill = Math.Clamp(fill, 0f, 1f);
        dl.AddRectFilled(At(x - 1f, y - 1f), At(x + w + 1f, y + h + 1f), 0xC0000000u);
        dl.AddRectFilled(At(x, y), At(x + w * fill, y + h), color);
    }

    /// <param name="degreeSign">Draw a small degree ring after the number.</param>
    private void DrawNumberCentered(ImDrawListPtr dl, int value, float areaX, float areaY, float areaW, bool degreeSign = false)
    {
        string text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        const float degreeW = 9f;
        float totalW = text.Length > 0
            ? text.Length * DigitCellW + (text.Length - 1) * DigitSpacing
            : 0f;
        if (degreeSign)
            totalW += degreeW;
        float x = MathF.Floor(areaX + (areaW - totalW) * 0.5f);
        float y = MathF.Floor(areaY);
        var cell = new Vector2(S(DigitCellW), S(DigitCellH));

        foreach (char c in text)
        {
            if (c == '-')
            {
                // The digit strip has no minus: draw one in the digits' green, at their middle height.
                float my = y + DigitCellH * 0.5f;
                dl.AddRectFilled(At(x + 3f, my - 2.5f), At(x + DigitCellW - 3f, my + 2.5f), 0xFF000000u, 1f);
                dl.AddRectFilled(At(x + 4f, my - 1.5f), At(x + DigitCellW - 4f, my + 1.5f), 0xFFAAFACDu);
            }
            else if (c is >= '0' and <= '9')
            {
                int idx = c - '0';
                // Half-texel inset prevents sampling bleed from neighbor digits.
                float texelU = 0.5f / DigitStripW;
                float u0 = (float)(idx * DigitCellW) / DigitStripW + texelU;
                float u1 = (float)((idx + 1) * DigitCellW) / DigitStripW - texelU;
                var min = At(x, y);
                dl.AddImage(_digitStripTex, min, min + cell, new Vector2(u0, 0f), new Vector2(u1, 1f));
            }
            x += DigitCellW + DigitSpacing;
        }

        if (degreeSign)
        {
            var ring = At(x + 3f, y + 4.5f);
            dl.AddCircle(ring, S(3.2f), 0xFF000000u, 16, S(3.5f));
            dl.AddCircle(ring, S(3.2f), 0xFFAAFACDu, 16, S(1.6f));
        }
    }
}
