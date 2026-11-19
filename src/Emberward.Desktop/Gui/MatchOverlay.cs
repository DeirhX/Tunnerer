namespace Emberward.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Emberward.Core;
using Emberward.Core.Entities;
using Emberward.Core.Match;
using Emberward.Core.Entities.Tanks;
using static Emberward.Desktop.Gui.HudDraw;

/// <summary>Death screen: what destroyed the tank and when it respawns; victory and defeat when a base falls or taps the core.</summary>
public static class MatchOverlay
{
    /// <param name="canRestart">Whether Enter starts a new match once this one is over.</param>
    /// <param name="winnerName">What to call a winner that is not the player; null for "another tank".</param>
    public static void Draw(
        (float x, float y, float w, float h) vp, Tank player, World world, bool canRestart = true, string? winnerName = null)
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
            DrawCenteredText(dl, 20f, cx, cy + 20f, VictoryLine(world), 0xFFFFFFFFu);
            if (canRestart)
                DrawCenteredText(dl, 20f, cx, cy + 50f, "Press Enter to play again", 0xFFC0E0FFu);
            return;
        }

        if (outcome == MatchOutcome.Defeat)
        {
            dl.AddRectFilled(new Vector2(vp.x, vp.y), new Vector2(vp.x + vp.w, vp.y + vp.h), 0x70000000u);
            DrawCenteredText(dl, 44f, cx, cy - 30f, "DEFEAT", 0xFF4040FFu);
            string why = player.Base?.IsDestroyed == true ? "Your base was destroyed"
                : world.MatchWinner?.Reason == MatchWinReason.TappedTheCore ? $"{winnerName ?? "Another tank"} tapped the core first"
                : "The enemy base is the last standing";
            DrawCenteredText(dl, 20f, cx, cy + 20f, why, 0xFFFFFFFFu);
            if (canRestart)
                DrawCenteredText(dl, 20f, cx, cy + 50f, "Press Enter to play again", 0xFFC0E0FFu);
            return;
        }

        if (!player.IsDead)
        {
            if (player.CanRecall)
                DrawCenteredText(dl, 20f, cx, cy, "Stranded. Press X to drop cargo and respawn", 0xFFC0E0FFu);
            return;
        }

        dl.AddRectFilled(new Vector2(vp.x, vp.y), new Vector2(vp.x + vp.w, vp.y + vp.h), 0x70000000u);
        string cause = player.DeathCause switch
        {
            DamageCause.EnemyFire => "Destroyed by enemy fire",
            DamageCause.Overheat => "Destroyed by overheating",
            DamageCause.Frostbite => "Destroyed by frostbite",
            _ => "Destroyed",
        };

        DrawCenteredText(dl, 44f, cx, cy - 30f, "TANK DESTROYED", 0xFF4060FFu);
        DrawCenteredText(dl, 20f, cx, cy + 20f, cause, 0xFFFFFFFFu);
        DrawCenteredText(dl, 20f, cx, cy + 48f,
            $"Respawning at base in {player.RespawnRemaining.TotalSeconds:0.0} s", 0xFFC0E0FFu);
    }

    /// <summary>Why the player won, in a line.</summary>
    public static string VictoryLine(World world) => world.MatchWinner?.Reason switch
    {
        MatchWinReason.TappedTheCore => "Your base tapped the core first",
        _ when world.TankList.Count > 2 => "Every other base was destroyed",
        _ => "The enemy base was destroyed",
    };
}
