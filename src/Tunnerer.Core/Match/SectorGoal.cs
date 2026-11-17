namespace Tunnerer.Core.Match;

using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Types;

/// <summary>
/// The sector goal (DESIGN.md §4.8): tap the hot core. Each tick it sums the charge each base's heat pumps near the
/// core made and advances that base's hold timer, which grows while the tap meets the goal rate and drains faster
/// while it falls short. The base that holds it long enough has tapped the core. A world without a core has no
/// sector goal and pays nothing for it.
/// </summary>
public sealed class SectorGoal
{
    // Scratch for the machines near the core, refilled every tick.
    private readonly List<Machine> _nearCore = new();

    public SectorGoal(Position core) => Core = core;

    /// <summary>Where the core is: the large hot rock pocket at the map centre, or the nearest spot to it clear of the bases.</summary>
    public Position Core { get; }

    /// <summary>The base that tapped the core first, among those whose tank still had lives left; null until one has.</summary>
    public TankBase? FirstToTapCore { get; private set; }

    public void Advance(IReadOnlyList<TankBase> bases, MachineList machines, IReadOnlyList<Tank> tanks, double dt)
    {
        const int radius = Tweaks.Goal.CoreTapRadius;
        const int radiusSq = radius * radius;
        // In list order, so each base's tap is summed in the same order on every machine.
        machines.CollectNear(Core, radius, _nearCore);
        foreach (var tankBase in bases)
        {
            double tap = 0.0;
            foreach (var machine in _nearCore)
                if (machine.IsAlive && machine.Behavior.IsHeatPump && machine.OwnerBase == tankBase &&
                    Position.DistanceSquared(machine.Position, Core) <= radiusSq)
                    tap += machine.LastChargeMade;
            tankBase.CoreTapPerTick = tap;
            tankBase.CoreHoldSeconds = NextHold(tankBase.CoreHoldSeconds, tap, dt);
            if (tankBase.CoreHoldSeconds >= Tweaks.Goal.GoalHoldSeconds)
            {
                tankBase.HasTappedCore = true;
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
