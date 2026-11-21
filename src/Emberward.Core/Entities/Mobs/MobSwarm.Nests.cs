namespace Emberward.Core.Entities.Mobs;

using Emberward.Core.Config;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

public sealed partial class MobSwarm
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
                nest.ThiefTicks = Tweaks.Mobs.ThiefHuntTicks;
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
        nest.Target = PickTarget(nest);
        _events.Record(new StepEvent(StepEventKind.NestWoke, nest.Centre, Count: nest.Inside));
    }

    private void AdvanceNest(int index, Nest nest)
    {
        if (nest.ThiefTicks > 0 && --nest.ThiefTicks == 0)
            nest.Thief = -1;
        if (nest.Alive == 0)
        {
            nest.Awake = false;
            return;
        }

        // Resting creatures drink heat whether the nest is awake or asleep.
        if (nest.Resting > 0 && (_tick + index) % Tweaks.Mobs.RechargeIntervalTicks == 0)
            Recharge(nest);

        if (!nest.Awake)
        {
            // Dormant: one distance test per tank, once in a while.
            if (nest.Inside > 0 && (_tick + index) % Tweaks.Mobs.WakeCheckTicks == 0 && SensesAnyone(nest))
                Wake(nest);
            return;
        }

        if ((_tick + index) % Tweaks.Mobs.WakeCheckTicks == 0)
            nest.Target = PickTarget(nest);
        bool hunting = nest.Target >= 0;
        // Nothing to hunt and nothing out: back to sleep, and the resting recharge on.
        if (!hunting && nest.Out == 0)
        {
            nest.Awake = false;
            return;
        }

        if (hunting && nest.Inside > 0 && --nest.ReleaseTimer <= 0)
        {
            Release(index, nest);
            nest.ReleaseTimer = Tweaks.Mobs.ReleaseIntervalTicks;
        }
    }

    private bool SensesAnyone(Nest nest)
    {
        var tanks = _tanks.Tanks;
        for (int i = 0; i < tanks.Count; i++)
        {
            Tank tank = tanks[i];
            if (tank.IsDead)
                continue;
            float range = SenseRange(tank.Heat);
            if (range > 0f && Position.DistanceSquared(tank.Position, nest.Centre) <= range * range)
                return true;
        }
        return false;
    }

    /// <summary>The nearest tank an awake nest hunts: one still warm enough within the leash, or the thief.</summary>
    private int PickTarget(Nest nest)
    {
        var tanks = _tanks.Tanks;
        int best = -1;
        long bestDistance = long.MaxValue;
        long leash = (long)Tweaks.Mobs.HuntLeashCells * Tweaks.Mobs.HuntLeashCells;
        for (int i = 0; i < tanks.Count; i++)
        {
            Tank tank = tanks[i];
            if (tank.IsDead)
                continue;
            long distance = Position.DistanceSquared(tank.Position, nest.Centre);
            bool thief = nest.Thief == i && nest.ThiefTicks > 0;
            bool scented = tank.Heat >= Tweaks.Mobs.LoseScentHeat && distance <= leash;
            if ((thief || scented) && distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }
        return best;
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
        _mobs.Add(new Mob
        {
            Id = _nextId++,
            Position = new PositionF(nest.Centre.X + angle * nest.Radius, nest.Centre.Y + (_rng.NextSingle() - 0.5f) * nest.Radius),
            Nest = index,
            Target = nest.Target,
            State = MobState.Hunting,
            Charge = charge,
        });
    }

    /// <summary>Resting creatures drink heat out of the deposit, up to their full charge between them.</summary>
    private void Recharge(Nest nest)
    {
        double want = nest.Resting * _fullCharge - nest.Stored;
        if (want <= 0.0)
            return;
        double limit = Tweaks.Mobs.RechargeCharges * _fullCharge;
        nest.Stored += _terrain.ExtractEnergyInRadiusArea(nest.Centre, nest.Radius, Math.Min(want, limit), TerrainGrid.ThermalField.Terrain);
    }
}
