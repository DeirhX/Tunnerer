namespace Tunnerer.Desktop.Gui;

using System.Numerics;
using ImGuiNET;

/// <summary>Drawing helpers the HUD pieces share: outlined text, colour packing, arcs and convex polygon bands.</summary>
public static class HudDraw
{
    // Text colours, from the shared HUD palette.
    public static readonly Vector4 TextGood = HudTheme.ToVector(HudTheme.Good);
    public static readonly Vector4 TextWarn = HudTheme.ToVector(HudTheme.Warn);
    public static readonly Vector4 TextBad = HudTheme.ToVector(HudTheme.Danger);
    public static readonly Vector4 TextInfo = HudTheme.ToVector(HudTheme.Teal);
    public static readonly Vector4 TextDim = HudTheme.ToVector(HudTheme.Text);

    /// <summary>An RGB colour (0-1) and alpha packed as ImGui's ABGR.</summary>
    public static uint PackColor(Vector3 rgb, float alpha) =>
        (uint)(Math.Clamp(alpha, 0f, 1f) * 255f) << 24 |
        (uint)(Math.Clamp(rgb.Z, 0f, 1f) * 255f) << 16 |
        (uint)(Math.Clamp(rgb.Y, 0f, 1f) * 255f) << 8 |
        (uint)(Math.Clamp(rgb.X, 0f, 1f) * 255f);

    public static void DrawOutlinedText(ImDrawListPtr dl, float size, Vector2 pos, string text, uint color, float outline = 1f)
    {
        var font = ImGui.GetFont();
        uint shadow = 0xE0000000u;
        dl.AddText(font, size, pos + new Vector2(outline, outline), shadow, text);
        dl.AddText(font, size, pos + new Vector2(-outline, outline), shadow, text);
        dl.AddText(font, size, pos + new Vector2(outline, -outline), shadow, text);
        dl.AddText(font, size, pos + new Vector2(-outline, -outline), shadow, text);
        dl.AddText(font, size, pos, color, text);
    }

    public static void DrawCenteredText(ImDrawListPtr dl, float size, float centerX, float y, string text, uint color)
    {
        var font = ImGui.GetFont();
        float w = font.CalcTextSizeA(size, float.MaxValue, 0f, text).X;
        DrawOutlinedText(dl, size, new Vector2(MathF.Floor(centerX - w * 0.5f), y), text, color);
    }

    /// <summary>A line of HUD text (outlined, never treated as a format string, so a literal '%' is shown).</summary>
    public static void ColoredText(Vector4 color, string text) =>
        HudTheme.LineRow(text, ImGui.ColorConvertFloat4ToU32(color), 16.5f);

    public static void StrokeArc(ImDrawListPtr dl, Vector2 c, float r, float a0, float a1, uint color, float thickness)
    {
        if (a1 <= a0)
            return;
        int segments = Math.Max(2, (int)((a1 - a0) / (MathF.PI * 2f) * 64f));
        dl.PathArcTo(c, r, a0, a1, segments);
        dl.PathStroke(color, ImDrawFlags.None, thickness);
    }

    /// <summary>Fills the part of a convex polygon between two heights.</summary>
    public static void FillPolygonBand(ImDrawListPtr dl, Vector2[] poly, float y0, float y1, uint color)
    {
        if (y1 <= y0)
            return;
        float x0 = float.MaxValue, x1 = float.MinValue;
        foreach (var p in poly)
        {
            x0 = MathF.Min(x0, p.X);
            x1 = MathF.Max(x1, p.X);
        }
        dl.PushClipRect(new Vector2(x0 - 1f, y0), new Vector2(x1 + 1f, y1), true);
        dl.AddConvexPolyFilled(ref poly[0], poly.Length, color);
        dl.PopClipRect();
    }

    /// <summary>Left and right x where a horizontal line crosses a convex polygon (equal if it misses).</summary>
    public static (float x0, float x1) PolygonSpanAtY(Vector2[] poly, float y)
    {
        float x0 = float.MaxValue, x1 = float.MinValue;
        for (int i = 0; i < poly.Length; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Length];
            if ((y < MathF.Min(a.Y, b.Y)) || (y > MathF.Max(a.Y, b.Y)) || a.Y == b.Y)
                continue;
            float x = a.X + (y - a.Y) / (b.Y - a.Y) * (b.X - a.X);
            x0 = MathF.Min(x0, x);
            x1 = MathF.Max(x1, x);
        }
        return x0 <= x1 ? (x0, x1) : (0f, 0f);
    }
}
