namespace Tunnerer.Core.Match;

using Tunnerer.Core.Entities.Tanks;
using Tunnerer.Core.Entities.Bases;
using Tunnerer.Core.Types;

/// <summary>
/// The result of one match: asks <see cref="MatchReferee"/> until it has an answer, then keeps that answer, so that
/// nothing that happens afterwards (the winner dying, another base finishing its hold) can change it.
/// </summary>
public sealed class MatchResult
{
    private readonly IReadOnlyList<Tank> _tanks;
    private bool _decided;
    private (Tank Tank, MatchWinReason Reason)? _winner;

    /// <param name="tanks">The match's tanks, a live list that the result reads whenever it is asked.</param>
    public MatchResult(IReadOnlyList<Tank> tanks) => _tanks = tanks;

    /// <summary>
    /// The tank that has won, and how. Null while the match is undecided, and after a match that ended with no tank
    /// left. Once <see cref="Latch"/> has recorded a result, it never changes.
    /// </summary>
    /// <param name="raceWinner">The base that won the race to the core, or null (also when the match is not a race).</param>
    public (Tank Tank, MatchWinReason Reason)? Winner(TankBase? raceWinner) =>
        _decided ? _winner : MatchReferee.Winner(_tanks, raceWinner);

    /// <summary>A match is over once a tank has won, or no tank has a life left. It stays over.</summary>
    public bool IsOver(TankBase? raceWinner) =>
        _decided || Winner(raceWinner) != null || MatchReferee.NobodyIsLeft(_tanks);

    /// <summary>Records the result the first time the referee has one; called once per step, after the goal has advanced.</summary>
    public void Latch(TankBase? raceWinner)
    {
        if (_decided)
            return;
        var winner = Winner(raceWinner);
        if (winner == null && !MatchReferee.NobodyIsLeft(_tanks))
            return;
        _winner = winner;
        _decided = true;
    }

    /// <summary>Mixes whether the result is fixed, and what it is, for the sync digest.</summary>
    internal ulong MixOutcome(ulong hash)
    {
        hash = Fnv1a.Mix(hash, _decided);
        return _winner is { } winner
            ? Fnv1a.Mix(Fnv1a.Mix(hash, winner.Tank.Color), (int)winner.Reason)
            : Fnv1a.Mix(hash, -1);
    }
}
