namespace Tunnerer.Core.Match;

using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Types;
using Tunnerer.Core.Entities.Tanks;
using Tunnerer.Core.Entities.Bases;

/// <summary>
/// The sector goal (DESIGN.md §4.8): tap the hot core. Each tick it sums the charge each base's heat pumps near the
/// core made and advances that base's hold timer, which grows while the tap meets the goal rate and drains faster
/// while it falls short. The base that holds it long enough has tapped the core. A world without a core has no
/// sector goal and pays nothing for it. Each base's progress is kept here (<see cref="TapOf"/>), not on the base.
/// </summary>
public sealed class SectorGoal
{
    // Scratch for the machines near the core, refilled every tick.
    private readonly List<Machine> _nearCore = new();
    private readonly IReadOnlyList<TankBase> _bases;
    // One per base, in the order of _bases. Grown by SyncTaps, so a base added after the goal was built still gets one.
    private readonly List<CoreTap> _taps = new();

    /// <param name="bases">The match's bases; the list may grow after this, and the goal follows it.</param>
    public SectorGoal(Position core, IReadOnlyList<TankBase> bases)
    {
        Core = core;
        _bases = bases;
    }

    private void SyncTaps()
    {
        while (_taps.Count < _bases.Count)
            _taps.Add(new CoreTap());
    }

    /// <summary>Where the core is: the large hot rock pocket at the map centre, or the nearest spot to it clear of the bases.</summary>
    public Position Core { get; }

    /// <summary>The base that tapped the core first, among those whose tank still had lives left; null until one has.</summary>
    public TankBase? FirstToTapCore { get; private set; }

    /// <summary>How <paramref name="tankBase"/> stands on the goal, or null for a base that is not in the match.</summary>
    public CoreTap? TapOf(TankBase tankBase)
    {
        SyncTaps();
        for (int i = 0; i < _taps.Count; i++)
            if (_bases[i] == tankBase)
                return _taps[i];
        return null;
    }

    public void Advance(MachineList machines, IReadOnlyList<Tank> tanks, double dt)
    {
        const int radius = Tweaks.Goal.CoreTapRadius;
        const int radiusSq = radius * radius;
        SyncTaps();
        // In list order, so each base's tap is summed in the same order on every machine.
        machines.CollectNear(Core, radius, _nearCore);
        for (int b = 0; b < _taps.Count; b++)
        {
            TankBase tankBase = _bases[b];
            CoreTap state = _taps[b];
            double tap = 0.0;
            foreach (var machine in _nearCore)
                if (machine.IsAlive && machine.Behavior.IsHeatPump && machine.OwnerBase == tankBase &&
                    Position.DistanceSquared(machine.Position, Core) <= radiusSq)
                    tap += machine.LastChargeMade;
            state.TapPerTick = tap;
            state.HoldSeconds = NextHold(state.HoldSeconds, tap, dt);
            if (state.HoldSeconds >= Tweaks.Goal.GoalHoldSeconds)
            {
                state.HasTapped = true;
                // Bases are visited in slot order, so two that finish on the same tick always resolve the same way.
                // A base whose tank is out of lives is out of the race, even if its hold was already running.
                if (!MatchReferee.IsEliminated(tanks, tankBase))
                    FirstToTapCore ??= tankBase;
            }
        }
    }

    /// <summary>
    /// One step of the core-tap hold timer: it grows by <paramref name="dt"/> while the tap meets the goal rate and
    /// drains <c>GoalHoldDecay</c> times as fast (never below zero) while it falls short.
    /// </summary>
    public static double NextHold(double hold, double tapPerTick, double dt)
    {
        double goalPerTick = Tweaks.Goal.GoalChargePerSecond * dt;
        return tapPerTick >= goalPerTick ? hold + dt : Math.Max(0.0, hold - dt * Tweaks.Goal.GoalHoldDecay);
    }
}

/// <summary>One base's progress on the sector goal (<see cref="SectorGoal.TapOf"/>, <see cref="World.CoreTapOf"/>).</summary>
public sealed class CoreTap
{
    /// <summary>Charge the base's heat pumps near the core made for it during the last tick.</summary>
    public double TapPerTick { get; internal set; }

    /// <summary>Seconds the tap has held the goal rate; the sector is won at <c>Tweaks.Goal.GoalHoldSeconds</c>.</summary>
    public double HoldSeconds { get; internal set; }

    /// <summary>True once the base has tapped the core at the goal rate for long enough. It stays true.</summary>
    public bool HasTapped { get; internal set; }
}
