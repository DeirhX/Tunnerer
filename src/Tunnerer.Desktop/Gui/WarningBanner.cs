namespace Tunnerer.Desktop.Gui;

using ImGuiNET;
using Tunnerer.Core.Entities;
using static Tunnerer.Desktop.Gui.HudDraw;

/// <summary>
/// One banner at a time at the top of the game view, most urgent first: overheating, then low shield, then running
/// hot. When each warning is on is decided by Core (<see cref="Tank.IsRunningHot"/>, <see cref="Tank.IsShieldLow"/>),
/// which the warning sounds also ask.
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
            DrawCenteredText(dl, 30f, cx, y, "OVERHEATING - SHIELD BURNING", blink ? 0xFF3030FFu : 0xFF8080FFu);
            DrawCenteredText(dl, 18f, cx, y + 34f, "Stop digging and firing, or return to base to cool down", 0xFFFFFFFFu);
        }
        else if (_shieldLowLatched)
        {
            DrawCenteredText(dl, 26f, cx, y, "SHIELD LOW", blink ? 0xFF3040FFu : 0xFF8080FFu);
            DrawCenteredText(dl, 18f, cx, y + 30f, "Your base repairs the shield", 0xFFFFFFFFu);
        }
        else if (_runningHotLatched)
        {
            DrawCenteredText(dl, 26f, cx, y, "RUNNING HOT", blink ? 0xFF30A0FFu : 0xFF80D0FFu);
            DrawCenteredText(dl, 18f, cx, y + 30f, "Digging and firing heat the tank - return to base to cool down", 0xFFFFFFFFu);
        }
        else if (player.SpawnProtectionTicks > 0)
        {
            DrawCenteredText(dl, 20f, cx, y, "Respawn shield active", 0xFFFFE65Au);
        }
        else if (time < _thermalHintUntil)
        {
            DrawCenteredText(dl, 20f, cx, y, "Press H for thermal view - enemy tunnels glow hot", 0xFF80E0FFu);
        }
    }
}
