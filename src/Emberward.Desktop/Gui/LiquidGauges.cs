namespace Emberward.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Emberward.Core.Entities.Tanks;
using static Emberward.Desktop.Gui.HudDraw;

/// <summary>
/// The shield crystal is dark above its liquid surface. A low shield pulses red and flashes when hit.
/// </summary>
public sealed class LiquidGauges(HudLayout layout)
{
    private static readonly Vector2[] ShieldOutline =
    [
        new(44f, 44f), new(60f, 40f), new(82f, 37f), new(104f, 40f), new(121f, 44f), new(121f, 80f),
        new(114f, 100f), new(100f, 116f), new(82f, 128f), new(64f, 116f), new(50f, 100f), new(44f, 80f),
    ];

    private float _shownShieldFill = -1f;

    public void Draw(ImDrawListPtr dl, Tank player, float shieldPanelX, float time)
    {
        float shieldFill = player.HullFraction;
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
        else if (player.IsHullLow(latched: false) && shieldFill > 0f && pulse)
            FillPolygonBand(dl, poly, sLevel, sBottom, 0x703030FFu);         // low shield (red pulse)
        FillPolygonBand(dl, poly, sTop, sLevel, 0xC8080808u);                // empty part
        var (sx0, sx1) = PolygonSpanAtY(poly, sLevel);
        if (sx1 > sx0)
            DrawSurface(dl, sLevel, (sx1 - sx0) * 0.5f, (sx0 + sx1) * 0.5f, shieldFill > 0f ? 0xFFFFE0A0u : 0u, time);
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
