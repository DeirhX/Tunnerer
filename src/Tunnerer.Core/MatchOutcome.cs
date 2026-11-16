namespace Tunnerer.Core;

/// <summary>How the match stands for one tank (see <see cref="World.MatchOutcomeFor"/>).</summary>
public enum MatchOutcome
{
    /// <summary>The match goes on for this tank.</summary>
    InProgress,

    /// <summary>Another tank is out of lives and this one is alive.</summary>
    Victory,

    /// <summary>This tank is dead with no lives left.</summary>
    Defeat,
}
