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
        Settle(nest, lull: 0);
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
            // Stored pyrite that found no room is tried again on the nest's check cadence, not every tick.
            if (nest.Pyrite > 0 && (_tick + index) % Tweaks.Emberites.WakeCheckTicks == 0)
                nest.Pyrite -= OreDrop.Scatter(_terrain, nest.Centre, nest.Pyrite, _isOccupied);
            return;
        }

        // Resting creatures drink heat whether the nest is awake or asleep, and starve where there is none.
        if (nest.Resting > 0 && (_tick + index) % Tweaks.Emberites.RechargeIntervalTicks == 0)
            Recharge(nest);

        if (!nest.Awake)
        {
            // Dormant: one distance test per tank, once in a while.
            if (Ready(nest) > 0 && (_tick + index) % Tweaks.Emberites.WakeCheckTicks == 0 && SensesAnyone(nest))
                Wake(nest);
            return;
        }

        if ((_tick + index) % Tweaks.Emberites.WakeCheckTicks == 0)
            nest.Target = PickTarget(nest, index);
        Tank? hunted = ValidTarget(nest.Target);
        // Nothing to hunt and nothing out: back to sleep, and the resting recharge on. A nest that still hunts stays
        // awake even with nothing left to let out, so that it keeps its target and wakes at once when heat returns.
        if (hunted is null && nest.Out == 0)
        {
            nest.Awake = false;
            Settle(nest, lull: 0);
            return;
        }
        AdvancePulse(index, nest, hunted);
    }

    /// <summary>
    /// A woken nest's pulse: quiet through the lull, then a stir (the warning), then the group, one creature every
    /// <see cref="Tweaks.Emberites.PulseSpacingTicks"/>. The lull runs on while the nest has lost its tank; a stir or
    /// a group in hand is dropped.
    /// </summary>
    private void AdvancePulse(int index, Nest nest, Tank? hunted)
    {
        if (nest.Phase == NestPhase.Quiet)
        {
            if (nest.ReleaseTimer > 0)
                nest.ReleaseTimer--;
            if (nest.ReleaseTimer == 0 && hunted != null && Ready(nest) > 0)
            {
                nest.Phase = NestPhase.Stirring;
                nest.ReleaseTimer = Tweaks.Emberites.StirTicks;
                _events.Record(new StepEvent(StepEventKind.NestStirred, nest.Centre, Count: Ready(nest)));
            }
            return;
        }
        if (hunted is null)
        {
            // Lost its tank: a stir comes to nothing, a group half out waits out a lull like a whole one.
            Settle(nest, nest.Phase == NestPhase.Releasing ? Tweaks.Emberites.LullTicks : 0);
            return;
        }
        if (nest.Phase == NestPhase.Stirring)
        {
            if (--nest.ReleaseTimer > 0)
                return;
            // The group is sized as the stir ends, so a tank that cooled through the warning meets fewer.
            nest.Phase = NestPhase.Releasing;
            nest.PulseLeft = PulseSize(hunted.Heat, Ready(nest));
        }
        if (nest.PulseLeft > 0 && Ready(nest) > 0 && --nest.ReleaseTimer <= 0)
        {
            Release(index, nest);
            nest.PulseLeft--;
            nest.ReleaseTimer = Tweaks.Emberites.PulseSpacingTicks;
        }
        if (nest.PulseLeft == 0 || Ready(nest) == 0)
            Settle(nest, Tweaks.Emberites.LullTicks);
    }

    /// <summary>Ends whatever pulse the nest was in, with <paramref name="lull"/> ticks before it may stir again.</summary>
    private static void Settle(Nest nest, int lull)
    {
        nest.Phase = NestPhase.Quiet;
        nest.PulseLeft = 0;
        nest.ReleaseTimer = lull;
    }

    /// <summary>
    /// How many creatures a pulse lets out at a tank of <paramref name="heat"/> when <paramref name="ready"/> could leave:
    /// <see cref="Tweaks.Emberites.PulseMinCreatures"/> at <see cref="Tweaks.Emberites.WakeHeat"/> or cooler, all of them at
    /// <see cref="Tweaks.Emberites.FullHeat"/>, linearly between.
    /// </summary>
    public static int PulseSize(float heat, int ready)
    {
        int least = Math.Min(Tweaks.Emberites.PulseMinCreatures, ready);
        float t = Math.Clamp((heat - Tweaks.Emberites.WakeHeat) / (Tweaks.Emberites.FullHeat - Tweaks.Emberites.WakeHeat), 0f, 1f);
        return least + (int)MathF.Round(t * (ready - least));
    }

    /// <summary>
    /// Creatures that could leave now: the fresh ones, and as many resting ones as the nest has full charges stored. A
    /// creature never leaves on part of a charge: it would bite as hard as a full one.
    /// </summary>
    private int Ready(Nest nest) => nest.NeverReleased + Math.Min(nest.Resting, FullCharges(nest));

    /// <summary>Whole charges in the nest's store. A hair under one counts: the grid hands heat back in single precision.</summary>
    private int FullCharges(Nest nest) => (int)(nest.Stored / _fullCharge + 1e-6);

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

    /// <summary>Lets one creature out: a fresh one makes its charge (booked as injected), a resting one takes a full charge of the nest's.</summary>
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
            charge = Math.Min(nest.Stored, _fullCharge);
            nest.Resting--;
            nest.Stored -= charge;
        }
        nest.Out++;
        float half = nest.Radius * 0.5f;
        _emberites.Add(new Emberite
        {
            Id = _nextId++,
            Position = new PositionF(nest.Centre.X + (_rng.NextSingle() * 2f - 1f) * half, nest.Centre.Y + (_rng.NextSingle() * 2f - 1f) * half),
            Nest = index,
            Target = nest.Target,
            State = EmberiteState.Hunting,
            Charge = charge,
        });
    }

    /// <summary>
    /// Resting creatures drink heat out of the deposit, up to their full charge between them. While one is short of a
    /// full charge and the nest fills none, it goes hungry, and after <see cref="Tweaks.Emberites.StarveTicks"/> the
    /// hungry die one by one. What a dead one leaves beyond the others' full charges goes back into the ground.
    /// </summary>
    private void Recharge(Nest nest)
    {
        int full = FullCharges(nest);
        if (full >= nest.Resting)
        {
            nest.HungerTicks = 0;
            return;
        }
        double want = nest.Resting * _fullCharge - nest.Stored;
        double limit = Tweaks.Emberites.RechargeCharges * _fullCharge;
        nest.Stored += _terrain.ExtractEnergyInRadiusArea(nest.Centre, nest.Radius, Math.Min(want, limit), TerrainGrid.ThermalField.Terrain);
        if (FullCharges(nest) > full)
        {
            nest.HungerTicks = 0;
            return;
        }
        nest.HungerTicks += Tweaks.Emberites.RechargeIntervalTicks;
        if (nest.HungerTicks < Tweaks.Emberites.StarveTicks)
            return;

        nest.HungerTicks = Tweaks.Emberites.StarveTicks - Tweaks.Emberites.StarveStepTicks;
        nest.Resting--;
        double spare = nest.Stored - nest.Resting * _fullCharge;
        if (spare > 0.0)
        {
            nest.Stored -= spare;
            _terrain.AddEnergyInRadiusArea(nest.Centre, nest.Radius, spare, TerrainGrid.ThermalField.Terrain);
        }
        _events.Record(new StepEvent(StepEventKind.EmberiteStarved, nest.Centre, Count: nest.Alive));
    }
}
