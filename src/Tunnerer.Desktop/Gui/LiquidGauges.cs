namespace Tunnerer.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Tunnerer.Core.Entities.Tanks;
using static Tunnerer.Desktop.Gui.HudDraw;

/// <summary>
/// Diablo-style liquid gauges in the art: the shield crystal and the materials chest are full up to their level
/// and dark above it, with a softly waving bright surface. A low shield pulses red and flashes when hit.
/// </summary>
public sealed class LiquidGauges(HudLayout layout)
{
    private static readonly Vector2[] ShieldOutline =
    [
        new(44f, 44f), new(60f, 40f), new(82f, 37f), new(104f, 40f), new(121f, 44f), new(121f, 80f),
        new(114f, 100f), new(100f, 116f), new(82f, 128f), new(64f, 116f), new(50f, 100f), new(44f, 80f),
    ];

    // The open top of the materials chest, where its contents show.
    private static readonly Vector2[] ChestOutline =
    [
        new(52f, 40f), new(193f, 40f), new(206f, 52f), new(206f, 86f),
        new(185f, 98f), new(60f, 98f), new(40f, 86f), new(40f, 52f),
    ];

    private float _shownShieldFill = -1f;
    private float _shownMaterialsFill = -1f;

    public void Draw(ImDrawListPtr dl, Tank player, float shieldPanelX, float materialsPanelX, float time)
    {
        float shieldFill = player.ShieldFraction;
        // Ease the shown levels toward the real ones so the liquid moves rather than jumps.
        _shownShieldFill = _shownShieldFill < 0f ? shieldFill : _shownShieldFill + (shieldFill - _shownShieldFill) * 0.20f;

        bool pulse = (int)(time * 4f) % 2 == 0;

        // Shield crystal.
        var o = new Vector2(shieldPanelX, 0f);
        var poly = new Vector2[ShieldOutline.Length];
        float sTop = float.MaxValue, sBottom = float.MinValue;
        for (int i = 0; i < poly.Length; i++)
        {
            poly[i] = layout.At(o + ShieldOutline[i]);
            sTop = MathF.Min(sTop, poly[i].Y);
            sBottom = MathF.Max(sBottom, poly[i].Y);
        }
        float sLevel = sBottom - (sBottom - sTop) * _shownShieldFill;
        if (player.HitFlashTicks > Tank.HitFlashDuration - 6)
            FillPolygonBand(dl, poly, sLevel, sBottom, 0x90FFFFFFu);         // hit flash
        else if (player.IsShieldLow(latched: false) && shieldFill > 0f && pulse)
            FillPolygonBand(dl, poly, sLevel, sBottom, 0x703030FFu);         // low shield (red pulse)
        FillPolygonBand(dl, poly, sTop, sLevel, 0xC8080808u);                // empty part
        var (sx0, sx1) = PolygonSpanAtY(poly, sLevel);
        if (sx1 > sx0)
            DrawSurface(dl, sLevel, (sx1 - sx0) * 0.5f, (sx0 + sx1) * 0.5f, shieldFill > 0f ? 0xFFFFE0A0u : 0u, time);

        // Materials chest.
        float materialsFill = player.HoldFill;
        _shownMaterialsFill = _shownMaterialsFill < 0f
            ? materialsFill
            : _shownMaterialsFill + (materialsFill - _shownMaterialsFill) * 0.15f;
        var chest = new Vector2[ChestOutline.Length];
        float cTop = float.MaxValue, cBottom = float.MinValue;
        for (int i = 0; i < chest.Length; i++)
        {
            chest[i] = layout.At(new Vector2(materialsPanelX, 0f) + ChestOutline[i]);
            cTop = MathF.Min(cTop, chest[i].Y);
            cBottom = MathF.Max(cBottom, chest[i].Y);
        }
        float cLevel = cBottom - (cBottom - cTop) * _shownMaterialsFill;
        FillPolygonBand(dl, chest, cTop, cLevel, 0xD8080808u);               // empty part
        var (cx0, cx1) = PolygonSpanAtY(chest, cLevel);
        if (cx1 > cx0)
            DrawSurface(dl, cLevel, (cx1 - cx0) * 0.5f, (cx0 + cx1) * 0.5f, materialsFill > 0f ? 0xFF80D8FFu : 0u, time);
    }

    /// <summary>A gently waving bright line where the liquid meets the empty part.</summary>
    private void DrawSurface(ImDrawListPtr dl, float y, float halfWidth, float centerX, uint color, float time)
    {
        if (color == 0u || halfWidth < layout.S(3f))
            return;
        const int segments = 12;
        var points = new Vector2[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            float u = i / (float)segments;
            float x = centerX - halfWidth + 2f * halfWidth * u;
            points[i] = new Vector2(x, y + MathF.Sin(u * 9f + time * 5f) * layout.S(1.2f));
        }
        dl.AddPolyline(ref points[0], points.Length, HudTheme.WithAlphaByte(color, 0x50), ImDrawFlags.None, layout.S(4f));
        dl.AddPolyline(ref points[0], points.Length, color, ImDrawFlags.None, layout.S(1.5f));
    }
}
