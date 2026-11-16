namespace Tunnerer.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Tunnerer.Desktop.Input;
using Tunnerer.Desktop.Config;

/// <summary>The terrain test window: shader pass toggles, the heat colormap and a screenshot button.</summary>
public static class TerrainTestPanel
{
    public static void Draw(
        (float x, float y, float w, float h) vp,
        bool heatDebugOverlayEnabled,
        PostProcessPassFlags passFlags,
        Action<string?> requestScreenshot,
        Action<GameCommand> executeCommand)
    {
        if (vp.w <= 0 || vp.h <= 0)
            return;

        ImGui.SetNextWindowPos(new Vector2(vp.x + 10f, vp.y + 10f), ImGuiCond.Once);
        ImGui.SetNextWindowBgAlpha(0.78f);
        ImGui.SetNextWindowSize(new Vector2(280f, 0f), ImGuiCond.Once);

        if (ImGui.Begin("Terrain Test", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.TextUnformatted("Shader Passes");
            ImGui.Separator();

            DrawToggleCheckbox("Native Smoothing", passFlags, PostProcessPassFlags.NativeTerrainSmoothing,
                GameCommand.TogglePostNativeTerrainSmoothing, executeCommand);
            DrawToggleCheckbox("Edge Curving", passFlags, PostProcessPassFlags.NativeEdgeCurving,
                GameCommand.TogglePostNativeEdgeCurving, executeCommand);
            DrawToggleCheckbox("Material Textures", passFlags, PostProcessPassFlags.TerrainAux,
                GameCommand.TogglePostTerrainAux, executeCommand);
            DrawToggleCheckbox("Vignette", passFlags, PostProcessPassFlags.Vignette,
                GameCommand.TogglePostVignette, executeCommand);
            DrawToggleCheckbox("Terrain Heat", passFlags, PostProcessPassFlags.TerrainHeat,
                GameCommand.TogglePostTerrainHeat, executeCommand);
            DrawToggleCheckbox("Tank Glow", passFlags, PostProcessPassFlags.TankGlow,
                GameCommand.TogglePostTankGlow, executeCommand);

            ImGui.Separator();
            bool heatDebug = heatDebugOverlayEnabled;
            if (ImGui.Checkbox("Heat Debug Colormap", ref heatDebug))
                executeCommand(GameCommand.ToggleHeatOverlay);

            ImGui.Separator();
            if (ImGui.Button("Screenshot (F10)"))
                requestScreenshot("terrain-test");

            ImGui.Separator();
            ImGui.TextDisabled("WASD to pan camera");
            ImGui.TextDisabled("1-7 hotkeys toggle passes");
        }
        ImGui.End();
    }

    private static void DrawToggleCheckbox(
        string label,
        PostProcessPassFlags currentFlags,
        PostProcessPassFlags targetFlag,
        GameCommand toggleCommand,
        Action<GameCommand> executeCommand)
    {
        bool enabled = (currentFlags & targetFlag) != 0;
        if (ImGui.Checkbox(label, ref enabled))
            executeCommand(toggleCommand);
    }
}
