namespace Emberward.Core.Entities.Tanks;

using Emberward.Core.Collision;
using Emberward.Core.Config;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Match;
using Emberward.Core.Snapshots;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

/// <summary>
/// The tank's reserve (DESIGN.md §4.9), in a world whose rules run it (<see cref="MatchRules.TankReserve"/>). The
/// reserve is the off-network clock of §4.6, not a second meter: the heat floor that rises while the tank is outside
/// its network. This system adds three things to it.
/// <list type="bullet">
/// <item>A burn. Off the network with emberite in the hold, the recall key burns a unit (on the first tick held, then
/// every <see cref="Tweaks.Reserve.BurnRepeatTicks"/>) instead of recalling: the floor falls by
/// <see cref="Tweaks.Reserve.FloorDropPerOre"/> degrees and the unit stops warming the tank. No heat is made or
/// destroyed: the floor only limits how far the tank can cool, and what holds it there is booked where it always was
/// (<see cref="TankThermals.Advance"/>). Once the hold is empty the key recalls again.</item>
/// <item>Stages. As the clock runs down the tank goes through warning, degraded and stranded before heat death
/// (<see cref="TankReserveStage"/>, <see cref="TankReserveEffects"/>); each stage counts its ticks.</item>
/// <item>A held clock. Within <see cref="Tweaks.Reserve.GeneratorHoldRadius"/> of a standing generator of its own side,
/// in line of sight, the floor of a tank off its network stops rising.</item>
/// </list>
/// Cost follows change: nothing runs but each live tank's step, and a generator is looked for near an off-network tank only.
/// </summary>
public sealed class TankReserveSystem
{
    private readonly MachineList _machines;
    private readonly ITerrainPixelReadOnly _terrain;

    // Each tank's reserve, by its colour (its spawn index); grown as tanks first act.
    private readonly List<TankState> _tanks = new();

    [NotSaved("scratch: filled and read within one question")]
    private readonly List<Machine> _near = new();

    public TankReserveSystem(MachineList machines, ITerrainPixelReadOnly terrain)
    {
        _machines = machines;
        _terrain = terrain;
    }

    /// <summary>
    /// Whether the recall key burns for <paramref name="tank"/> instead of recalling: alive, off its network, with
    /// emberite in the hold and a floor high enough that a unit buys back its whole worth (so a held key wastes none
    /// of the hold just outside the network).
    /// </summary>
    public static bool CanBurn(Tank tank) =>
        !tank.IsDead && tank.IsOffNetwork && tank.OreHeld > 0 && tank.OffNetworkFloor >= Tweaks.Reserve.FloorDropPerOre;

    /// <summary>The stage <paramref name="tank"/>'s clock puts it in now.</summary>
    public static TankReserveStage StageOf(Tank tank)
    {
        if (tank.IsDead || !tank.IsOffNetwork)
            return TankReserveStage.None;
        float seconds = tank.OffNetworkSecondsToOverheat;
        return seconds <= 0f ? TankReserveStage.HeatDeath
            : seconds <= Tweaks.Reserve.StrandedSeconds ? TankReserveStage.Stranded
            : seconds <= Tweaks.Reserve.DegradedSeconds ? TankReserveStage.Degraded
            : seconds <= Tweaks.Reserve.WarningSeconds ? TankReserveStage.Warning
            : TankReserveStage.None;
    }

    /// <summary>
    /// Whether a generator holds <paramref name="tank"/>'s clock: a standing generator of its side within
    /// <see cref="Tweaks.Reserve.GeneratorHoldRadius"/>, with open ground between them. Asked by
    /// <see cref="TankThermals.AdvanceNetworkCoverage"/> for a tank off its network.
    /// </summary>
    internal bool HoldsClock(Tank tank)
    {
        int slot = SlotOf(tank);
        TankState state = _tanks[slot];
        state.ClockHeld = tank.Base is { IsDestroyed: false } home && IsNearGenerator(tank, home);
        _tanks[slot] = state;
        return state.ClockHeld;
    }

    private bool IsNearGenerator(Tank tank, Entities.Bases.TankBase home)
    {
        int radius = Tweaks.Reserve.GeneratorHoldRadius;
        _machines.CollectNear(tank.Position, radius, _near, inListOrder: false);
        bool held = false;
        foreach (var machine in _near)
        {
            if (!machine.Behavior.IsGenerator || !machine.StandsFor(home) ||
                Position.DistanceSquared(machine.Position, tank.Position) > radius * radius ||
                !Raycaster.HasOpenLine(_terrain, machine.Position, tank.Position))
                continue;
            held = true;
            break;
        }
        _near.Clear();
        return held;
    }

    /// <summary>
    /// One tick of <paramref name="tank"/>'s reserve, from <see cref="Tank.Advance"/> once its coverage is known: burns
    /// a unit while the recall key is held (<paramref name="burnHeld"/>), then settles its stage and returns what the
    /// stage does to the tank this tick.
    /// </summary>
    internal TankReserveEffects Advance(Tank tank, bool burnHeld, StepEventLog? events)
    {
        int slot = SlotOf(tank);
        TankState state = _tanks[slot];

        if (burnHeld && CanBurn(tank))
        {
            if (state.BurnHeldTicks % Tweaks.Reserve.BurnRepeatTicks == 0 && tank.Hold.Pay(1))
            {
                tank.LowerOffNetworkFloor(Tweaks.Reserve.FloorDropPerOre);
                events?.Record(new StepEvent(StepEventKind.ReserveBurned, tank.Position, tank.Color, Count: 1));
            }
            state.BurnHeldTicks++;
        }
        else
        {
            state.BurnHeldTicks = 0;
        }

        // Only an off-network tank asks whether a generator holds its clock; back on the network nothing does.
        if (!tank.IsOffNetwork)
            state.ClockHeld = false;
        var stage = StageOf(tank);
        if (stage != state.Stage)
        {
            state.Stage = stage;
            state.StageTicks = 0;
            events?.Record(new StepEvent(StepEventKind.ReserveStageChanged, tank.Position, tank.Color, Count: (int)stage));
        }
        else
        {
            state.StageTicks++;
        }
        _tanks[slot] = state;
        return TankReserveEffects.For(stage);
    }

    /// <summary>
    /// Whether <paramref name="tank"/>, blocked by the ground, digs this tick: always, unless its stage slows the dig,
    /// when the ticks it digs on add up to the stage's share.
    /// </summary>
    internal bool TryDig(Tank tank)
    {
        float factor = TankReserveEffects.For(StageOf(tank)).DigFactor;
        if (factor >= 1f)
            return true;
        int slot = SlotOf(tank);
        TankState state = _tanks[slot];
        state.DigCredit += factor;
        bool digs = state.DigCredit >= 1f;
        if (digs)
            state.DigCredit -= 1f;
        _tanks[slot] = state;
        return digs;
    }

    /// <summary>The percentage of its sight radius <paramref name="tank"/> sees in its stage.</summary>
    public int SightPercent(Tank tank) => TankReserveEffects.For(StageOf(tank)).SightPercent;

    /// <summary>What the HUD and the bot read of <paramref name="tank"/>'s reserve (<see cref="TankReserveView"/>).</summary>
    public TankReserveView ViewOf(Tank tank)
    {
        if (tank.IsDead || !tank.IsOffNetwork)
            return default;
        TankState state = tank.Color >= 0 && tank.Color < _tanks.Count ? _tanks[tank.Color] : default;
        var stage = StageOf(tank);
        float left = tank.OffNetworkSecondsToOverheat;
        float next = stage switch
        {
            TankReserveStage.None => left - Tweaks.Reserve.WarningSeconds,
            TankReserveStage.Warning => left - Tweaks.Reserve.DegradedSeconds,
            TankReserveStage.Degraded => left - Tweaks.Reserve.StrandedSeconds,
            TankReserveStage.Stranded => left,
            _ => 0f,
        };
        float inStage = state.Stage == stage ? state.StageTicks / (float)Tweaks.Perf.TargetFps : 0f;
        return new TankReserveView(true, stage, left, Math.Max(0f, next), inStage, CanBurn(tank),
            Tweaks.Reserve.FloorDropPerOre / tank.OffNetworkHeatPerSecond, state.ClockHeld);
    }

    /// <summary>Mixes every tank's reserve, for <see cref="WorldStateHash.Compute"/>.</summary>
    internal ulong MixState(ulong hash)
    {
        hash = Fnv1a.Mix(hash, _tanks.Count);
        foreach (var state in _tanks)
        {
            hash = Fnv1a.Mix(hash, (int)state.Stage);
            hash = Fnv1a.Mix(hash, state.StageTicks);
            hash = Fnv1a.Mix(hash, state.BurnHeldTicks);
            hash = Fnv1a.Mix(hash, state.DigCredit);
            hash = Fnv1a.Mix(hash, state.ClockHeld);
        }
        return hash;
    }

    private int SlotOf(Tank tank)
    {
        int slot = Math.Max(0, tank.Color);
        while (_tanks.Count <= slot)
            _tanks.Add(default);
        return slot;
    }

    /// <summary>
    /// A tank's reserve: its stage and the ticks it has spent in it (the stage's counter), the ticks the burn key has
    /// been held, its dig credit while a stage slows the dig, and whether a generator held its clock last tick.
    /// </summary>
    private struct TankState
    {
        public TankReserveStage Stage;
        public int StageTicks;
        public int BurnHeldTicks;
        public float DigCredit;
        public bool ClockHeld;
    }
}
