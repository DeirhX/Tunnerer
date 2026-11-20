namespace Emberward.Core.Match;

using Emberward.Core.Entities.Tanks;
using Emberward.Core.Entities.Bases;
using Emberward.Core.Types;

/// <summary>
/// The result of one match: asks <see cref="MatchReferee"/> until it has an answer, then keeps that answer, so that
/// nothing that happens afterwards (the winner dying, another base finishing its hold) can change it.
/// </summary>
public sealed class MatchResult
{
    private readonly IReadOnlyList<Tank> _tanks;
    private bool _decided;

    // The winner's place in the tank list, not the tank: a snapshot then reaches each tank through the list, where the
    // loading world has the tank to fill in, and not first through this field, where it has none.
    private (int TankIndex, MatchWinReason Reason)? _winner;

    /// <param name="tanks">The match's tanks, a live list that the result reads whenever it is asked.</param>
    public MatchResult(IReadOnlyList<Tank> tanks) => _tanks = tanks;

    /// <summary>
    /// The tank that has won, and how. Null while the match is undecided, and after a match that ended with every base
    /// destroyed. Once <see cref="Latch"/> has recorded a result, it never changes.
    /// </summary>
    /// <param name="raceWinner">The base that won the race to the core, or null (also when the match is not a race).</param>
    public (Tank Tank, MatchWinReason Reason)? Winner(TankBase? raceWinner) =>
        _decided
            ? _winner is { } winner ? (_tanks[winner.TankIndex], winner.Reason) : null
            : MatchReferee.Winner(_tanks, raceWinner);

    /// <summary>A match is over once a tank has won, or every base is destroyed. It stays over.</summary>
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
        _winner = winner is { } won ? (IndexOf(won.Tank), won.Reason) : null;
        _decided = true;
    }

    private int IndexOf(Tank tank)
    {
        for (int i = 0; i < _tanks.Count; i++)
            if (_tanks[i] == tank)
                return i;
        throw new InvalidOperationException("The winner is not one of the match's tanks.");
    }

    /// <summary>Mixes whether the result is fixed, and what it is, for the sync digest.</summary>
    internal ulong MixOutcome(ulong hash)
    {
        hash = Fnv1a.Mix(hash, _decided);
        return _winner is { } winner
            ? Fnv1a.Mix(Fnv1a.Mix(hash, _tanks[winner.TankIndex].Color), (int)winner.Reason)
            : Fnv1a.Mix(hash, -1);
    }
}
