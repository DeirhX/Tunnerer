namespace Emberward.Core.Match;

using Emberward.Core.Entities.Tanks;
using Emberward.Core.Entities.Bases;

/// <summary>How the match stands for one tank (see <see cref="World.MatchOutcomeFor"/>).</summary>
public enum MatchOutcome
{
    InProgress,
    Victory,
    Defeat,
}

/// <summary>How a match was won (see <see cref="World.MatchWinner"/>).</summary>
public enum MatchWinReason
{
    LastBaseStanding,
    TappedTheCore,
}

/// <summary>Only losing a base eliminates a side. In a core race the first intact base to tap the core wins.</summary>
public static class MatchReferee
{
    public static bool IsEliminated(Tank tank) => tank.Base?.IsDestroyed == true;

    public static bool NobodyIsLeft(IReadOnlyList<Tank> tanks)
    {
        foreach (var tank in tanks)
            if (!IsEliminated(tank))
                return false;
        return tanks.Count > 0;
    }

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
            if (IsEliminated(tank))
                continue;
            last = tank;
            left++;
        }
        return left == 1 && tanks.Count > 1 ? (last!, MatchWinReason.LastBaseStanding) : null;
    }

    public static MatchOutcome OutcomeFor(Tank player, Tank? winner)
    {
        if (winner == player)
            return MatchOutcome.Victory;
        return winner != null || IsEliminated(player) ? MatchOutcome.Defeat : MatchOutcome.InProgress;
    }
}
