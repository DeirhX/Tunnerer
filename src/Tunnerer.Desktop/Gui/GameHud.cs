namespace Tunnerer.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Tunnerer.Core;
using Tunnerer.Core.Entities;
using Tunnerer.Desktop.Input;
using Tunnerer.Desktop.Config;
using Tunnerer.Desktop.Rendering;

/// <summary>
/// Draws the in-game HUD each frame: the game view, crosshair and hit feedback here, and the pieces in their own
/// files (<see cref="HudStrip"/>, <see cref="NetworkPanel"/>, <see cref="WarningBanner"/>, <see cref="MatchOverlay"/>,
/// <see cref="PostPassOverlay"/>, <see cref="TerrainTestPanel"/>), plus the tutorial and notifications
/// (<c>GameHud.Guidance.cs</c>) and the upgrade panel and goal banner (<c>GameHud.Goal.cs</c>).
/// </summary>
public partial class GameHud
{
    private readonly HudStrip _strip = new();
    private readonly NetworkPanel _network = new();
    private readonly WarningBanner _warnings = new();
    private float _time;

    public (float x, float y, float w, float h) ViewportRect { get; private set; }
    public (float x, float y)? CrosshairScreenPos { get; set; }

    /// <summary>State of the temperature orb shader for the frame just drawn; the game passes it to the backend.</summary>
    public HeatOrbState HeatOrb => _strip.HeatOrb;

    public void Init(nint heatIcon, nint shieldIcon, nint panelFrame, nint buildPanel, nint digitStrip) =>
        _strip.Init(heatIcon, shieldIcon, panelFrame, buildPanel, digitStrip);

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
        DrawGameViewport(gameTextureId, texW, texH);
        DrawCrosshair();
        DrawHitFeedback(player);
        _strip.Draw(player, _time);
        _network.Draw(ViewportRect, player, world, _time);
        _warnings.Draw(ViewportRect, player, heatDebugOverlayEnabled, _time);
        DrawGuidance(player, world);
        DrawUpgradePanel(player);
        DrawSectorComplete(player);
        MatchOverlay.Draw(ViewportRect, player, world);
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
            ImGui.Image(gameTextureId, new Vector2(texW, texH));
            ViewportRect = (cursorPos.X, cursorPos.Y, texW, texH);
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    /// <summary>Bottom of the visible game view: the top of the HUD strip, or the view's own bottom if that is higher.</summary>
    private float ViewBottom((float x, float y, float w, float h) vp) =>
        _strip.Top > vp.y ? MathF.Min(_strip.Top, vp.y + vp.h) : vp.y + vp.h;

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

    /// <summary>Red edge vignette after a hit, with a wedge pointing back towards where the shot came from.</summary>
    private void DrawHitFeedback(Tank player)
    {
        var vp = ViewportRect;
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
