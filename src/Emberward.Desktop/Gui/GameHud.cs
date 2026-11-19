namespace Emberward.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Emberward.Core;
using Emberward.Desktop.Input;
using Emberward.Desktop.Config;
using Emberward.Desktop.Rendering;
using Emberward.Core.Entities.Tanks;

/// <summary>
/// Draws the in-game HUD each frame: the game view, crosshair and hit feedback here, and the pieces in their own
/// files (<see cref="HudStrip"/>, <see cref="NetworkPanel"/>, <see cref="WarningBanner"/>, <see cref="MatchOverlay"/>,
/// <see cref="PostPassOverlay"/>, <see cref="TerrainTestPanel"/>), plus the tutorial and notifications
/// (<c>GameHud.Guidance.cs</c>), the upgrade panel and goal banner (<c>GameHud.Goal.cs</c>) and the charge labels over
/// machines (<c>GameHud.Power.cs</c>).
/// </summary>
public partial class GameHud
{
    private readonly HudStrip _strip = new();
    private readonly NetworkPanel _network = new();
    private float _time;

    public (float x, float y, float w, float h) ViewportRect { get; private set; }
    public (float x, float y)? CrosshairScreenPos { get; set; }

    /// <summary>State of the temperature orb shader for the frame just drawn; the game passes it to the backend.</summary>
    public HeatOrbState HeatOrb => _strip.HeatOrb;

    public void Init(nint heatIcon, nint shieldIcon, nint panelFrame, nint orePile, nint buildPanel) =>
        _strip.Init(heatIcon, shieldIcon, panelFrame, orePile, buildPanel);

    /// <summary>
    /// Device pixel (the game texture, the camera, the mouse over the view) to UI units. A UI unit is
    /// <see cref="ImGuiPlatform.DeviceScale"/> device pixels, so the game texture is drawn 1:1 with no resampling.
    /// </summary>
    private static float ToUi(float devicePixels) => devicePixels / ImGuiPlatform.DeviceScale;

    /// <summary>
    /// False where Enter does not start a new match: in a network match that is still on for the others, when
    /// this player's own tank is already out.
    /// </summary>
    public bool CanRestartMatch { get; set; } = true;

    /// <summary>
    /// True in a network lobby, before the match starts: the view is dimmed and none of the panels, gauges or guidance
    /// are drawn, so the lobby plate is all there is to read.
    /// </summary>
    public bool WaitingForPlayers { get; set; }

    /// <summary>What to call the tank that won, when it is not the player's; null for "another tank".</summary>
    public string? WinnerName { get; set; }

    /// <summary>Where a point in the world (in cells) is on screen, in UI units.</summary>
    public Vector2 CellToScreen(float cellX, float cellY)
    {
        var vp = ViewportRect;
        return new Vector2(
            vp.x + ToUi(cellX * Camera.Scale - Camera.X),
            vp.y + ToUi(cellY * Camera.Scale - Camera.Y));
    }

    /// <summary>True when a screen point lies inside the game view.</summary>
    public bool IsInView(Vector2 screen)
    {
        var vp = ViewportRect;
        return screen.X >= vp.x && screen.Y >= vp.y && screen.X <= vp.x + vp.w && screen.Y <= vp.y + vp.h;
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
        _time += MathF.Max(0f, deltaTime);
        _guidance.Update(world, player, _time, offerThermalView: !heatDebugOverlayEnabled);
        DrawGameViewport(gameTextureId, texW, texH);
        if (WaitingForPlayers)
            return;
        DrawPowerLabels(player, world);
        DrawCrosshair();
        DrawHitFeedback(player);
        _strip.Draw(player, _time, world.Rules);
        _network.Draw(ViewportRect, player, world, _guidance.Hint);
        WarningBanner.Draw(ViewportRect, _guidance.Warning, _time);
        DrawGuidance();
        DrawUpgradePanel(player);
        DrawSectorComplete(player, world);
        MatchOverlay.Draw(PlayRect, player, world, CanRestartMatch, WinnerName);
        if (showPostPassOverlay)
            PostPassOverlay.Draw(ViewportRect, heatDebugOverlayEnabled, postPassFlags);
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
        DrawGameViewport(gameTextureId, texW, texH);
        TerrainTestPanel.Draw(ViewportRect, heatDebugOverlayEnabled, passFlags, requestScreenshot, executeCommand);
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
            ImGui.Image(gameTextureId, new Vector2(ToUi(texW), ToUi(texH)));
            ViewportRect = (cursorPos.X, cursorPos.Y, ToUi(texW), ToUi(texH));
            // Drawn into for the rest of the frame by what belongs on the game view under every panel (charge labels).
            _viewportDrawList = ImGui.GetWindowDrawList();
            if (WaitingForPlayers)
                ImGui.GetWindowDrawList().AddRectFilled(Vector2.Zero, displaySize, 0xC0000000u);
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    /// <summary>
    /// The part of the view above the HUD strip. Overlays that cover the whole view (the death veil, the hit vignette,
    /// the key reminder) use it, so they do not sit on the HUD plates.
    /// </summary>
    public (float x, float y, float w, float h) PlayRect
    {
        get
        {
            var vp = ViewportRect;
            return (vp.x, vp.y, vp.w, ViewBottom(vp) - vp.y);
        }
    }

    /// <summary>Bottom of the visible game view: the top of the HUD strip, or the view's own bottom if that is higher.</summary>
    private float ViewBottom((float x, float y, float w, float h) vp) =>
        _strip.Top > vp.y ? MathF.Min(_strip.Top, vp.y + vp.h) : vp.y + vp.h;

    private void DrawCrosshair()
    {
        if (CrosshairScreenPos is not { } window) return;
        var pos = (x: ToUi(window.x), y: ToUi(window.y));
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

    /// <summary>Red edge vignette after a hit, with a wedge pointing back towards where the shot came from.</summary>
    private void DrawHitFeedback(Tank player)
    {
        var vp = PlayRect;
        if (vp.w <= 0 || player.IsDead || player.HitFlashTicks <= 0)
            return;

        float k = player.HitFlashTicks / (float)Tank.HitFlashDuration;
        uint edge = (uint)(k * 150f) << 24 | 0x0000E0u;
        const uint clear = 0x000000E0u;
        var dl = ImGui.GetForegroundDrawList();
        float t = MathF.Min(vp.w, vp.h) * 0.12f;
        float x0 = vp.x, y0 = vp.y, x1 = vp.x + vp.w, y1 = vp.y + vp.h;
        dl.AddRectFilledMultiColor(new Vector2(x0, y0), new Vector2(x1, y0 + t), edge, edge, clear, clear);
        dl.AddRectFilledMultiColor(new Vector2(x0, y1 - t), new Vector2(x1, y1), clear, clear, edge, edge);
        dl.AddRectFilledMultiColor(new Vector2(x0, y0), new Vector2(x0 + t, y1), edge, clear, clear, edge);
        dl.AddRectFilledMultiColor(new Vector2(x1 - t, y0), new Vector2(x1, y1), clear, edge, edge, clear);

        var v = player.LastHitVelocity;
        float len = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
        if (len < 1e-3f)
            return;
        // The shot travelled along v, so it came from -v.
        var dir = new Vector2(-v.X / len, -v.Y / len);
        var side = new Vector2(-dir.Y, dir.X);
        var center = new Vector2(vp.x + vp.w * 0.5f, vp.y + vp.h * 0.5f);
        float r = MathF.Min(vp.w, vp.h) * 0.22f;
        var tip = center + dir * (r + 26f);
        var b1 = center + dir * r + side * 22f;
        var b2 = center + dir * r - side * 22f;
        uint wedge = (uint)(k * 230f) << 24 | 0x3040FFu;
        dl.AddTriangleFilled(tip, b1, b2, wedge);
    }
}
