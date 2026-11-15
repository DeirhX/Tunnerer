namespace Tunnerer.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Tunnerer.Core;
using Tunnerer.Core.Entities;
using static Tunnerer.Desktop.Gui.HudDraw;

/// <summary>Death screen: what destroyed the tank and when it respawns; victory and defeat when lives run out.</summary>
public static class MatchOverlay
{
    public static void Draw((float x, float y, float w, float h) vp, Tank player, World world)
    {
        if (vp.w <= 0)
            return;

        MatchOutcome outcome = world.MatchOutcomeFor(player);

        var dl = ImGui.GetForegroundDrawList();
        float cx = vp.x + vp.w * 0.5f;
        float cy = vp.y + vp.h * 0.24f;

        if (outcome == MatchOutcome.Victory)
        {
            DrawCenteredText(dl, 44f, cx, cy - 30f, "VICTORY", 0xFF40FF80u);
            DrawCenteredText(dl, 20f, cx, cy + 20f, "The enemy tank is out of lives", 0xFFFFFFFFu);
            DrawCenteredText(dl, 20f, cx, cy + 50f, "Press Enter to play again", 0xFFC0E0FFu);
            return;
        }

        if (!player.IsDead)
            return;

        dl.AddRectFilled(new Vector2(vp.x, vp.y), new Vector2(vp.x + vp.w, vp.y + vp.h), 0x70000000u);
        string cause = player.DeathCause switch
        {
            DamageCause.EnemyFire => "Destroyed by enemy fire",
            DamageCause.Overheat => "Destroyed by overheating",
            _ => "Destroyed",
        };

        if (outcome == MatchOutcome.Defeat)
        {
            DrawCenteredText(dl, 44f, cx, cy - 30f, "DEFEAT", 0xFF4040FFu);
            DrawCenteredText(dl, 20f, cx, cy + 20f, cause + " - no lives left", 0xFFFFFFFFu);
            DrawCenteredText(dl, 20f, cx, cy + 50f, "Press Enter to play again", 0xFFC0E0FFu);
            return;
        }

        DrawCenteredText(dl, 44f, cx, cy - 30f, "TANK DESTROYED", 0xFF4060FFu);
        DrawCenteredText(dl, 20f, cx, cy + 20f, cause, 0xFFFFFFFFu);
        string lives = player.LivesLeft == 1 ? "1 life left" : $"{player.LivesLeft} lives left";
        DrawCenteredText(dl, 20f, cx, cy + 48f,
            $"Respawning at base in {player.RespawnRemaining.TotalSeconds:0.0} s  -  {lives}", 0xFFC0E0FFu);
    }
}
