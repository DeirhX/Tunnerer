namespace Tunnerer.Desktop.Gui;

using System.Numerics;
using ImGuiNET;
using Tunnerer.Core;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Match;
using static Tunnerer.Desktop.Gui.HudDraw;

/// <summary>Death screen: what destroyed the tank and when it respawns; victory and defeat when lives run out.</summary>
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

        // Still alive, but another tank won the race to the core.
        if (outcome == MatchOutcome.Defeat && !MatchReferee.IsOutOfLives(player))
        {
            dl.AddRectFilled(new Vector2(vp.x, vp.y), new Vector2(vp.x + vp.w, vp.y + vp.h), 0x70000000u);
            DrawCenteredText(dl, 44f, cx, cy - 30f, "DEFEAT", 0xFF4040FFu);
            DrawCenteredText(dl, 20f, cx, cy + 20f, $"{winnerName ?? "Another tank"} tapped the core first", 0xFFFFFFFFu);
            if (canRestart)
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
            DamageCause.Frostbite => "Destroyed by frostbite",
            _ => "Destroyed",
        };

        if (outcome == MatchOutcome.Defeat)
        {
            DrawCenteredText(dl, 44f, cx, cy - 30f, "DEFEAT", 0xFF4040FFu);
            DrawCenteredText(dl, 20f, cx, cy + 20f, cause + " - no lives left", 0xFFFFFFFFu);
            if (canRestart)
                DrawCenteredText(dl, 20f, cx, cy + 50f, "Press Enter to play again", 0xFFC0E0FFu);
            return;
        }

        DrawCenteredText(dl, 44f, cx, cy - 30f, "TANK DESTROYED", 0xFF4060FFu);
        DrawCenteredText(dl, 20f, cx, cy + 20f, cause, 0xFFFFFFFFu);
        string lives = player.LivesLeft == 1 ? "1 life left" : $"{player.LivesLeft} lives left";
        DrawCenteredText(dl, 20f, cx, cy + 48f,
            $"Respawning at base in {player.RespawnRemaining.TotalSeconds:0.0} s  -  {lives}", 0xFFC0E0FFu);
    }

    /// <summary>Why the player won, in a line.</summary>
    public static string VictoryLine(World world) => world.MatchWinner?.Reason switch
    {
        MatchWinReason.TappedTheCore => "Your base tapped the core first",
        _ when world.TankList.Count > 2 => "Every other tank is out of lives",
        _ => "The enemy tank is out of lives",
    };
}
