namespace Tunnerer.Core;

using Tunnerer.Core.Entities;

/// <summary>How the match stands for one tank (see <see cref="World.MatchOutcomeFor"/>).</summary>
public enum MatchOutcome
{
    /// <summary>The match goes on for this tank.</summary>
    InProgress,

    /// <summary>This tank has won: it is the last with lives left, or the first to tap the core in a race.</summary>
    Victory,

    /// <summary>This tank is dead with no lives left, or another tank has won.</summary>
    Defeat,
}

/// <summary>How a match was won (see <see cref="World.MatchWinner"/>).</summary>
public enum MatchWinReason
{
    /// <summary>Every other tank is out of lives.</summary>
    LastTankStanding,

    /// <summary>Its base tapped the core before any other.</summary>
    TappedTheCore,
}

/// <summary>
/// The rules that decide a match, for any number of tanks: a tank with no lives left is out, the last one in wins,
/// and in a race the first base to tap the core wins outright.
/// </summary>
public static class MatchReferee
{
    public static bool IsOutOfLives(Tank tank) => tank.IsDead && tank.LivesLeft <= 0;

    public static bool NobodyIsLeft(IReadOnlyList<Tank> tanks)
    {
        foreach (var tank in tanks)
            if (!IsOutOfLives(tank))
                return false;
        return tanks.Count > 0;
    }

    /// <param name="firstToTapCore">The base that won the race to the core, or null (also when it is not a race).</param>
    public static (Tank Tank, MatchWinReason Reason)? Winner(IReadOnlyList<Tank> tanks, TankBase? firstToTapCore)
    {
        if (firstToTapCore != null)
            foreach (var tank in tanks)
                if (tank.Base == firstToTapCore)
                    return (tank, MatchWinReason.TappedTheCore);

        Tank? last = null;
        int left = 0;
        foreach (var tank in tanks)
        {
            if (IsOutOfLives(tank))
                continue;
            last = tank;
            left++;
        }
        // A tank alone on the map has nobody to outlast.
        return left == 1 && tanks.Count > 1 ? (last!, MatchWinReason.LastTankStanding) : null;
    }

    public static MatchOutcome OutcomeFor(Tank player, Tank? winner)
    {
        if (IsOutOfLives(player))
            return MatchOutcome.Defeat;
        if (winner == null)
            return MatchOutcome.InProgress;
        if (winner != player)
            return MatchOutcome.Defeat;
        // The winner's victory waits for its respawn, so the death screen is not cut short.
        return player.IsDead ? MatchOutcome.InProgress : MatchOutcome.Victory;
    }
}
