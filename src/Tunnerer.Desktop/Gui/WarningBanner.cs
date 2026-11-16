namespace Tunnerer.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Tunnerer.Core.Entities;
using Tunnerer.Desktop.Config;

/// <summary>
/// One banner at a time at the top of the game view, most urgent first: overheating, frostbite, chilled, low shield,
/// running hot. When each warning is on is decided by Core (<see cref="Tank.IsRunningHot"/>,
/// <see cref="Tank.IsShieldLow"/>), which the warning sounds also ask.
/// </summary>
public sealed class WarningBanner
{
    private bool _runningHotLatched;
    private bool _shieldLowLatched;
    private float _thermalHintUntil = -1f;
    private bool _thermalHintShown;

    public void Draw((float x, float y, float w, float h) vp, Tank player, bool heatDebugOverlayEnabled, float time)
    {
        if (vp.w <= 0 || player.IsDead)
        {
            _runningHotLatched = _shieldLowLatched = false;
            return;
        }

        _runningHotLatched = player.IsRunningHot(_runningHotLatched);
        _shieldLowLatched = player.IsShieldLow(_shieldLowLatched);

        // After the first hit, teach the thermal view once: enemy tunnels glow in it.
        if (!_thermalHintShown && player.HitFlashTicks > 0 && !heatDebugOverlayEnabled)
        {
            _thermalHintShown = true;
            _thermalHintUntil = time + 8f;
        }

        var dl = ImGui.GetForegroundDrawList();
        float cx = vp.x + vp.w * 0.5f;
        float y = vp.y + 18f;
        bool blink = (int)(time * 3f) % 2 == 0;

        if (player.IsOverheating)
        {
            DrawBanner(dl, cx, y, "OVERHEATING - SHIELD BURNING", 30f, "Stop digging and firing, or return to base to cool down", HudTheme.Danger, blink);
        }
        else if (player.IsFreezing)
        {
            DrawBanner(dl, cx, y, "FROSTBITE - SHIELD CRACKING", 30f, "Get away from the frost, or return to base to warm up", HudTheme.Teal, blink);
        }
        else if (player.SpeedFactor < 0.999f)
        {
            int percent = (int)MathF.Round(player.SpeedFactor * 100f);
            DrawBanner(dl, cx, y, $"CHILLED - {percent}% SPEED", 24f, null, HudTheme.Teal, false);
        }
        else if (_shieldLowLatched)
        {
            DrawBanner(dl, cx, y, "SHIELD LOW", 26f, "Your base repairs the shield", HudTheme.Danger, blink);
        }
        else if (_runningHotLatched)
        {
            DrawBanner(dl, cx, y, "RUNNING HOT", 26f, "Digging and firing heat the tank - return to base to cool down", HudTheme.Warn, blink);
        }
        else if (player.SpawnProtectionTicks > 0)
        {
            DrawBanner(dl, cx, y, "Respawn shield active", 20f, null, HudTheme.Teal, false);
        }
        else if (time < _thermalHintUntil)
        {
            DrawBanner(dl, cx, y, "Press H for thermal view - enemy tunnels glow hot", 20f, null, HudTheme.Amber, false);
        }
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
        uint titleColor = blink ? accent : (accent & 0x00FFFFFFu) | 0xC0000000u;
        HudTheme.TextAt(dl, titleSize, new Vector2(MathF.Floor(cx - titleSz.X * 0.5f), y + 3f), title, titleColor);
        if (sub != null)
            HudTheme.TextAt(dl, subSize, new Vector2(MathF.Floor(cx - subSz.X * 0.5f), y + 9f + titleSz.Y), sub, HudTheme.Text);
    }
}
