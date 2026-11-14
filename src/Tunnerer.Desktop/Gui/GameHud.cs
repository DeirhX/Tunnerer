namespace Tunnerer.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Desktop.Config;
using Tunnerer.Desktop.Input;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Desktop.Rendering;

public class GameHud
{
    public const float BottomPanelHeight = 223f;

    private nint _energyIconTex, _shieldIconTex;
    private nint _panelFrameTex, _buildPanelTex;
    private nint _digitStripTex;

    private const int DigitCount = 10;
    private const int DigitCellW = 17;
    private const int DigitCellH = 20;
    private const int DigitStripW = DigitCellW * DigitCount; // 170
    private const float DigitSpacing = 1f;

    public (float x, float y, float w, float h) ViewportRect { get; private set; }
    public (float x, float y)? CrosshairScreenPos { get; set; }

    public void Init(nint energyIcon, nint shieldIcon, nint panelFrame, nint buildPanel, nint digitStrip)
    {
        _energyIconTex = energyIcon;
        _shieldIconTex = shieldIcon;
        _panelFrameTex = panelFrame;
        _buildPanelTex = buildPanel;
        _digitStripTex = digitStrip;
    }

    public void Draw(
        nint gameTextureId,
        int texW,
        int texH,
        Tank player,
        World world,
        float deltaTime,
        bool heatDebugOverlayEnabled,
        PostProcessPassFlags postPassFlags,
        bool showPostPassOverlay)
    {
        _ = deltaTime;
        DrawGameViewport(gameTextureId, texW, texH);
        DrawCrosshair();
        DrawBottomPanel(player);
        DrawHeatNetworkOverlay(player, world);
        if (showPostPassOverlay)
            DrawPostPassOverlay(heatDebugOverlayEnabled, postPassFlags);
    }

    private void DrawGameViewport(nint gameTextureId, int texW, int texH)
    {
        var displaySize = ImGui.GetIO().DisplaySize;

        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(displaySize.X, displaySize.Y), ImGuiCond.Always);

        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                    ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse |
                    ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoBackground;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);

        if (ImGui.Begin("##Viewport", flags))
        {
            var cursorPos = ImGui.GetCursorScreenPos();
            ImGui.SetCursorPos(Vector2.Zero);
            ImGui.Image(gameTextureId, new Vector2(texW, texH));
            ViewportRect = (cursorPos.X, cursorPos.Y, texW, texH);
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    private void DrawCrosshair()
    {
        if (CrosshairScreenPos is not { } pos) return;
        var vp = ViewportRect;
        if (pos.x < vp.x || pos.y < vp.y || pos.x >= vp.x + vp.w || pos.y >= vp.y + vp.h) return;

        var dl = ImGui.GetForegroundDrawList();
        const float armLen = 10f;
        const float gap = 3f;
        const uint col = 0xFFFFFFFF;
        dl.AddLine(new Vector2(pos.x - armLen, pos.y), new Vector2(pos.x - gap, pos.y), col, 2f);
        dl.AddLine(new Vector2(pos.x + gap, pos.y), new Vector2(pos.x + armLen, pos.y), col, 2f);
        dl.AddLine(new Vector2(pos.x, pos.y - armLen), new Vector2(pos.x, pos.y - gap), col, 2f);
        dl.AddLine(new Vector2(pos.x, pos.y + gap), new Vector2(pos.x, pos.y + armLen), col, 2f);
    }

    private void DrawBottomPanel(Tank player)
    {
        var displaySize = ImGui.GetIO().DisplaySize;
        var dl = ImGui.GetForegroundDrawList();

        const float eW = 245f, sW = 145f, mW = 230f, bW = 404f;
        const float h = 223f;
        const float stripW = eW + sW + mW + bW; // 1024

        float startX = MathF.Floor((displaySize.X - stripW) * 0.5f);
        float drawY = displaySize.Y - h;

        float x = startX;
        dl.AddImage(_energyIconTex, new Vector2(x, drawY), new Vector2(x + eW, drawY + h));
        x += eW;
        dl.AddImage(_shieldIconTex, new Vector2(x, drawY), new Vector2(x + sW, drawY + h));
        x += sW;
        dl.AddImage(_panelFrameTex, new Vector2(x, drawY), new Vector2(x + mW, drawY + h));
        x += mW;
        dl.AddImage(_buildPanelTex, new Vector2(x, drawY), new Vector2(x + bW, drawY + h));

        // Heat budget left before overheating (more than 100 while the tank carries a coolant charge).
        int heat = (int)MathF.Max(0f, MathF.Round(player.HeatBudget));
        float plateX = startX + 105f;
        float plateW = 130f;
        float plateY = drawY + 168f;
        DrawNumberCentered(dl, heat, plateX, plateY, plateW);

        // Shield/health value: inside the blue label plate below "SHIELD" text
        int health = player.Reactor.Health;
        float sPlateX = startX + eW + 10f;
        float sPlateW = 125f;
        float sPlateY = drawY + 166f;
        DrawNumberCentered(dl, health, sPlateX, sPlateY, sPlateW);
    }

    private void DrawNumberCentered(ImDrawListPtr dl, int value, float areaX, float areaY, float areaW)
    {
        string text = value.ToString();
        float totalW = text.Length > 0
            ? text.Length * DigitCellW + (text.Length - 1) * DigitSpacing
            : 0f;
        float x = MathF.Floor(areaX + (areaW - totalW) * 0.5f);
        float y = MathF.Floor(areaY);

        foreach (char c in text)
        {
            if (c is >= '0' and <= '9')
            {
                int idx = c - '0';
                // Half-texel inset prevents sampling bleed from neighbor digits.
                float texelU = 0.5f / DigitStripW;
                float u0 = (float)(idx * DigitCellW) / DigitStripW + texelU;
                float u1 = (float)((idx + 1) * DigitCellW) / DigitStripW - texelU;
                dl.AddImage(_digitStripTex,
                    new Vector2(x, y),
                    new Vector2(x + DigitCellW, y + DigitCellH),
                    new Vector2(u0, 0f),
                    new Vector2(u1, 1f));
            }
            x += DigitCellW + DigitSpacing;
        }
    }

    public void DrawTerrainTest(
        nint gameTextureId,
        int texW,
        int texH,
        bool heatDebugOverlayEnabled,
        PostProcessPassFlags passFlags,
        Action<string?> requestScreenshot,
        Action<GameCommand> executeCommand)
    {
        DrawGameViewportFullscreen(gameTextureId, texW, texH);
        DrawTerrainTestPanel(heatDebugOverlayEnabled, passFlags, requestScreenshot, executeCommand);
    }

    private void DrawGameViewportFullscreen(nint gameTextureId, int texW, int texH)
    {
        var displaySize = ImGui.GetIO().DisplaySize;

        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(displaySize.X, displaySize.Y), ImGuiCond.Always);

        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                    ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse |
                    ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoBackground;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);

        if (ImGui.Begin("##Viewport", flags))
        {
            var cursorPos = ImGui.GetCursorScreenPos();
            ImGui.SetCursorPos(Vector2.Zero);
            ImGui.Image(gameTextureId, new Vector2(texW, texH));
            ViewportRect = (cursorPos.X, cursorPos.Y, texW, texH);
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    private void DrawTerrainTestPanel(
        bool heatDebugOverlayEnabled,
        PostProcessPassFlags passFlags,
        Action<string?> requestScreenshot,
        Action<GameCommand> executeCommand)
    {
        var vp = ViewportRect;
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
            DrawToggleCheckbox("Terrain Curve (Post)", passFlags, PostProcessPassFlags.TerrainCurve,
                GameCommand.TogglePostTerrainCurve, executeCommand);
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

    /// <summary>
    /// Top-right readout of the heat network: how full the base reservoir is, how many of the player's relays are
    /// connected, and what the build key (E) will do next.
    /// </summary>
    private void DrawHeatNetworkOverlay(Tank player, World world)
    {
        var vp = ViewportRect;
        var tankBase = player.Base;
        if (vp.w <= 0 || vp.h <= 0 || tankBase == null)
            return;

        int relays = 0, powered = 0;
        foreach (var machine in world.Machines.Machines)
        {
            if (!machine.IsAlive || machine.Type != MachineType.Relay || machine.State != MachineState.Planted ||
                machine.OwnerBase != tankBase)
                continue;
            relays++;
            if (machine.LinkPoint?.IsPowered == true)
                powered++;
        }

        int capacity = Math.Max(1, (int)tankBase.Reactor.HeatCapacity);
        float fill = Math.Clamp((int)tankBase.Reactor.Heat / (float)capacity, 0f, 1f);
        var fillColor = new Vector4(0.35f + 0.65f * fill, 0.95f - 0.65f * fill, 0.30f, 1f);

        string buildHint = player.IsCarryingMachine
            ? "E: plant relay (outside base)"
            : player.Resources.Current.Dirt >= Tweaks.Machine.RelayDirtCost
                ? $"E: build relay ({Tweaks.Machine.RelayDirtCost} dirt)"
                : $"relay needs {Tweaks.Machine.RelayDirtCost} dirt ({player.Resources.Current.Dirt})";

        ImGui.SetNextWindowPos(new Vector2(vp.x + vp.w - 10f, vp.y + 10f), ImGuiCond.Always, new Vector2(1f, 0f));
        ImGui.SetNextWindowBgAlpha(0.60f);
        var flags = ImGuiWindowFlags.NoDecoration |
                    ImGuiWindowFlags.NoInputs |
                    ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoSavedSettings;

        if (ImGui.Begin("##HeatNetworkOverlay", flags))
        {
            ImGui.SetWindowFontScale(1.25f);
            ImGui.TextUnformatted("Heat network");
            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.45f, 0.85f, 1f, 1f), $"Tank coolant {(int)(player.CoolantFraction * 100f)}%");
            ImGui.TextColored(fillColor, $"Base reservoir {(int)(fill * 100f)}%");
            ImGui.TextUnformatted($"Relays {powered}/{relays} connected");
            ImGui.TextUnformatted(buildHint);
            ImGui.SetWindowFontScale(1.0f);
        }
        ImGui.End();
    }

    private void DrawPostPassOverlay(bool heatDebugOverlayEnabled, PostProcessPassFlags passFlags)
    {
        var vp = ViewportRect;
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
            DrawPassStatusLine(passFlags, PostProcessPassFlags.TerrainCurve, "2 TerrainCurve");
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
        Vector4 color = enabled
            ? new Vector4(0.36f, 0.96f, 0.49f, 1.0f)
            : new Vector4(0.92f, 0.33f, 0.33f, 1.0f);
        ImGui.TextColored(color, $"{label}: {(enabled ? "ON" : "OFF")}");
    }

    private static uint ToAbgr(float r, float g, float b, float a)
    {
        byte rb = (byte)(MathF.Min(r, 1f) * 255 + 0.5f);
        byte gb = (byte)(MathF.Min(g, 1f) * 255 + 0.5f);
        byte bb = (byte)(MathF.Min(b, 1f) * 255 + 0.5f);
        byte ab = (byte)(MathF.Min(a, 1f) * 255 + 0.5f);
        return (uint)(ab << 24 | bb << 16 | gb << 8 | rb);
    }
}
