namespace Emberward.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Emberward.Desktop.Config;
using Emberward.Desktop.Gui.Guidance;

/// <summary>
/// One banner at a time at the top of the game view, most urgent first. Which warning is on is chosen by
/// <see cref="PlayerWarnings"/>, shared with the browser; this only draws it.
/// </summary>
public static class WarningBanner
{
    public static void Draw((float x, float y, float w, float h) vp, PlayerWarning? warning, float time)
    {
        if (vp.w <= 0 || warning is null)
            return;

        var dl = ImGui.GetForegroundDrawList();
        float cx = vp.x + vp.w * 0.5f;
        float y = vp.y + 18f;
        bool blink = warning.Blinks && (int)(time * 3f) % 2 == 0;
        float titleSize = warning.Kind switch
        {
            WarningKind.Overheating or WarningKind.Frostbite or WarningKind.FireAboard => 30f,
            WarningKind.HullLow or WarningKind.RunningHot => 26f,
            WarningKind.Chilled => 24f,
            _ => 20f,
        };
        uint accent = warning.Tone switch
        {
            HintTone.Bad => HudTheme.Danger,
            HintTone.Warn => HudTheme.Warn,
            HintTone.Accent => HudTheme.Amber,
            _ => HudTheme.Teal,
        };
        DrawBanner(dl, cx, y, warning.Title, titleSize, warning.Detail, accent, blink);
    }

    /// <summary>A warning banner at the top of the view: a themed plate with a title and an optional second line.</summary>
    private static void DrawBanner(ImDrawListPtr dl, float cx, float y, string title, float titleSize, string? sub, uint accent, bool blink)
    {
        const float subSize = 17f;
        var titleSz = HudTheme.Measure(titleSize, title);
        var subSz = sub != null ? HudTheme.Measure(subSize, sub) : Vector2.Zero;
        float w = MathF.Max(titleSz.X, subSz.X) + 56f;
        float h = titleSz.Y + (sub != null ? subSz.Y + 6f : 0f) + 22f;
        var min = new Vector2(MathF.Floor(cx - w * 0.5f), y - 8f);
        HudTheme.DrawPanel(dl, min, min + new Vector2(w, h), accent);
        uint titleColor = blink ? accent : HudTheme.WithAlphaByte(accent, 0xC0);
        HudTheme.TextAt(dl, titleSize, new Vector2(MathF.Floor(cx - titleSz.X * 0.5f), y + 3f), title, titleColor);
        if (sub != null)
            HudTheme.TextAt(dl, subSize, new Vector2(MathF.Floor(cx - subSz.X * 0.5f), y + 9f + titleSz.Y), sub, HudTheme.Text);
    }
}
