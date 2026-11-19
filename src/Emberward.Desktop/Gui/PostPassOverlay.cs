namespace Emberward.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Emberward.Desktop.Config;
using static Emberward.Desktop.Gui.HudDraw;

/// <summary>Renderer debug readout (F11): which post passes are on.</summary>
public static class PostPassOverlay
{
    public static void Draw((float x, float y, float w, float h) vp, bool heatDebugOverlayEnabled, PostProcessPassFlags passFlags)
    {
        if (vp.w <= 0 || vp.h <= 0)
            return;

        ImGui.SetNextWindowPos(new Vector2(vp.x + 10f, vp.y + 10f), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.60f);
        var flags = ImGuiWindowFlags.NoDecoration |
                    ImGuiWindowFlags.NoInputs |
                    ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoSavedSettings;

        if (ImGui.Begin("##PostPassOverlay", flags))
        {
            ImGui.SetWindowFontScale(1.25f);
            ImGui.TextUnformatted("Post Passes");
            ImGui.Separator();
            DrawPassStatusLine(passFlags, PostProcessPassFlags.Vignette, "1 Vignette");
            DrawPassStatusLine(passFlags, PostProcessPassFlags.TerrainAux, "3 TerrainTex");
            DrawPassStatusLine(passFlags, PostProcessPassFlags.TankGlow, "4 TankGlow");
            DrawPassStatusLine(passFlags, PostProcessPassFlags.NativeTerrainSmoothing, "5 NativeSmoothing");
            DrawPassStatusLine(passFlags, PostProcessPassFlags.TerrainHeat, "6 TerrainHeat");
            DrawPassStatusLine(passFlags, PostProcessPassFlags.NativeEdgeCurving, "7 NativeEdgeCurving");
            DrawToggleStatusLine(heatDebugOverlayEnabled, "8 HeatColormap (Shift+1)");
            DrawPassStatusLine(passFlags, PostProcessPassFlags.ThermalRegions, "9 ThermalRegions (Shift+2)");
            ImGui.SetWindowFontScale(1.0f);
        }
        ImGui.End();
    }

    private static void DrawPassStatusLine(PostProcessPassFlags passFlags, PostProcessPassFlags pass, string label)
    {
        bool enabled = (passFlags & pass) != 0;
        DrawToggleStatusLine(enabled, label);
    }

    private static void DrawToggleStatusLine(bool enabled, string label)
    {
        ColoredText(enabled ? HudTheme.Good : HudTheme.Danger, $"{label}: {(enabled ? "ON" : "OFF")}");
    }
}
