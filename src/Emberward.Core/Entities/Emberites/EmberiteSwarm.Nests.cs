namespace Emberward.Core.Entities.Emberites;

using Emberward.Core.Collision;
using Emberward.Core.Config;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Resources;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

public sealed partial class EmberiteSwarm
{
    /// <summary>
    /// Digging a nest's ore wakes it whatever the tank's heat, and the thief is hunted for a while. Reads only the
    /// step's events, so it costs nothing in a step that dug none.
    /// </summary>
    private void WatchForThieves()
    {
        foreach (StepEvent e in _events.Events)
        {
            if (e.Kind != StepEventKind.OreCollected)
                continue;
            int tank = TankIndexOfColor(e.Color);
            if (tank < 0)
                continue;
            foreach (Nest nest in _nests)
            {
                if (nest.IsCleared || Position.DistanceSquared(e.Position, nest.Centre) > nest.Radius * nest.Radius)
                    continue;
                nest.Thief = tank;
                nest.ThiefTicks = Tweaks.Emberites.ThiefHuntTicks;
                Wake(nest);
            }
        }
    }

    private int TankIndexOfColor(int color)
    {
        var tanks = _tanks.Tanks;
        for (int i = 0; i < tanks.Count; i++)
            if (tanks[i].Color == color)
                return i;
        return -1;
    }

    private void Wake(Nest nest)
    {
        if (nest.Awake)
            return;
        nest.Awake = true;
        nest.ReleaseTimer = 0;
        nest.Target = PickTarget(nest, -1);
        _events.Record(new StepEvent(StepEventKind.NestWoke, nest.Centre, Count: nest.Inside));
    }

    private void AdvanceNest(int index, Nest nest)
    {
        if (nest.ThiefTicks > 0 && --nest.ThiefTicks == 0)
            nest.Thief = -1;
        if (nest.Alive == 0)
        {
            nest.Awake = false;
            if (nest.Pyrite > 0)
                nest.Pyrite -= OreDrop.Scatter(_terrain, nest.Centre, nest.Pyrite, _isOccupied);
            return;
        }

        // Resting creatures drink heat whether the nest is awake or asleep.
        if (nest.Resting > 0 && (_tick + index) % Tweaks.Emberites.RechargeIntervalTicks == 0)
            Recharge(nest);

        if (!nest.Awake)
        {
            // Dormant: one distance test per tank, once in a while.
            if (nest.Inside > 0 && CanRelease(nest) && (_tick + index) % Tweaks.Emberites.WakeCheckTicks == 0 && SensesAnyone(nest))
                Wake(nest);
            return;
        }

        if ((_tick + index) % Tweaks.Emberites.WakeCheckTicks == 0)
            nest.Target = PickTarget(nest, index);
        bool hunting = nest.Target >= 0;
        // Nothing to hunt and nothing out: back to sleep, and the resting recharge on. A nest that still hunts stays
        // awake even with nothing left to let out, so that it keeps its target and wakes at once when heat returns.
        if (!hunting && nest.Out == 0)
        {
            nest.Awake = false;
            return;
        }

        if (hunting && CanRelease(nest) && nest.Inside > 0 && --nest.ReleaseTimer <= 0)
        {
            Release(index, nest);
            nest.ReleaseTimer = Tweaks.Emberites.ReleaseIntervalTicks;
        }
    }

    /// <summary>
    /// A creature can come out while the nest has a fresh one left, or heat stored for its resting ones to take. A
    /// resting creature with nothing stored would come out with no charge, go home and be let out again: a loop.
    /// </summary>
    private static bool CanRelease(Nest nest) => nest.NeverReleased > 0 || nest.Stored > 0.0;

    private bool SensesAnyone(Nest nest)
    {
        var tanks = _tanks.Tanks;
        for (int i = 0; i < tanks.Count; i++)
        {
            Tank tank = tanks[i];
            if (tank.IsDead)
                continue;
            float range = SenseRange(tank.Heat);
            if (range > 0f && Position.DistanceSquared(tank.Position, nest.Centre) <= range * range &&
                Raycaster.HasCreatureLine(_terrain, tank.Position, nest.Centre))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The nearest tank an awake nest hunts: one still warm enough within its heat leash (<see cref="HeatLeash"/>),
    /// one a creature of the nest has a clear line to, or the thief. <paramref name="index"/> is the nest's own index
    /// (-1 to leave sight out, as at the moment of waking, when no creature is out to look).
    /// </summary>
    private int PickTarget(Nest nest, int index)
    {
        var tanks = _tanks.Tanks;
        int best = -1;
        long bestDistance = long.MaxValue;
        for (int i = 0; i < tanks.Count; i++)
        {
            Tank tank = tanks[i];
            if (tank.IsDead)
                continue;
            long distance = Position.DistanceSquared(tank.Position, nest.Centre);
            if (distance >= bestDistance)
                continue;
            bool thief = nest.Thief == i && nest.ThiefTicks > 0;
            float leash = HeatLeash(tank.Heat);
            if (thief || (leash > 0f && distance <= leash * leash) || (index >= 0 && SeenByCreature(index, i)))
            {
                best = i;
                bestDistance = distance;
            }
        }
        return best;
    }

    /// <summary>Whether a creature of nest <paramref name="nest"/> has lately had a clear line to the tank <paramref name="tank"/>.</summary>
    private bool SeenByCreature(int nest, int tank)
    {
        var span = EmberiteSpan;
        for (int i = 0; i < span.Length; i++)
        {
            ref readonly Emberite emberite = ref span[i];
            if (emberite.Nest == nest && emberite.Target == tank && emberite.Sees && !emberite.Gone && _tick - emberite.SightTick <= Tweaks.Emberites.SightCheckTicks)
                return true;
        }
        return false;
    }

    /// <summary>Lets one creature out: a fresh one makes its charge (booked as injected), a resting one takes its share of the nest's.</summary>
    private void Release(int index, Nest nest)
    {
        double charge;
        if (nest.NeverReleased > 0)
        {
            nest.NeverReleased--;
            charge = _fullCharge;
            _terrain.Ledger.RecordInjected(charge);
        }
        else
        {
            charge = nest.Resting == 1 ? nest.Stored : nest.Stored / nest.Resting;
            nest.Resting--;
            nest.Stored -= charge;
        }
        nest.Out++;
        float angle = _rng.NextSingle() - 0.5f;
        _emberites.Add(new Emberite
        {
            Id = _nextId++,
            Position = new PositionF(nest.Centre.X + angle * nest.Radius, nest.Centre.Y + (_rng.NextSingle() - 0.5f) * nest.Radius),
            Nest = index,
            Target = nest.Target,
            State = EmberiteState.Hunting,
            Charge = charge,
        });
    }

    /// <summary>Resting creatures drink heat out of the deposit, up to their full charge between them.</summary>
    private void Recharge(Nest nest)
    {
        double want = nest.Resting * _fullCharge - nest.Stored;
        if (want <= 0.0)
            return;
        double limit = Tweaks.Emberites.RechargeCharges * _fullCharge;
        nest.Stored += _terrain.ExtractEnergyInRadiusArea(nest.Centre, nest.Radius, Math.Min(want, limit), TerrainGrid.ThermalField.Terrain);
    }
}
