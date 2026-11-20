namespace Emberward.Core.Entities.Machines;

using System.Collections.Immutable;
using Emberward.Core.Types;
using Emberward.Core.Config;
using Emberward.Core.Resources;
using Emberward.Core.Terrain;
using Emberward.Core.Network;
using Emberward.Core.Match;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Entities.Projectiles;

/// <summary>What a machine's action can see and touch.</summary>
/// <param name="IsOccupied">
/// True when the position would put the given machine on top of a tank or another machine; a roaming harvester
/// checks it before every step.
/// </param>
/// <param name="Machines">Every machine in the world, in a fixed order, and filed by place: pylons look for batteries, harvesters for pylons.</param>
/// <param name="CoverageOf">
/// The coverage of its own base's linked network where the machine stands (DESIGN.md §4.6), with the node that covers
/// it: a harvester only delivers ore from there, and on the energy network takes its charge through that node.
/// </param>
/// <param name="Events">Where a machine records what it did this step (<see cref="World.StepEvents"/>). Null records nothing.</param>
/// <param name="Tanks">Every tank, in spawn order: a turret looks for enemies among them. Null shoots at nothing.</param>
/// <param name="Projectiles">Where a turret's shots fly. Null fires nothing.</param>
/// <param name="Upgrades">
/// Per-machine upgrades when the match rules run them (<see cref="Match.MatchRules.MachineUpgrades"/>): a relay asks it
/// its pump rate. Null leaves every machine at its base numbers.
/// </param>
/// <param name="Energy">
/// The energy network (DESIGN.md §4.9) when the match rules run it (<see cref="Match.MatchRules.EnergyNetwork"/>):
/// machines then spend charge through it, and pylons and harvester refuel trips stand down. Null runs the battery
/// rules of §4.5.
/// </param>
public readonly record struct MachineContext(
    ITerrainAccess Terrain, Func<Machine, Position, bool> IsOccupied, MachineList Machines,
    Func<Machine, TankCoverage> CoverageOf, StepEventLog? Events = null, EnergyNetwork? Energy = null, BasePowerSystem? BasePower = null,
    MachineUpgradeSystem? Upgrades = null, TankList? Tanks = null, ProjectileList? Projectiles = null);

/// <summary>
/// Who an island of the energy network feeds first when it runs short (DESIGN.md §4.9): base defence, then turrets,
/// then economy. Fixed by machine type.
/// </summary>
public enum PowerTier { BaseDefence, Turrets, Economy }

/// <summary>
/// A machine's capacitor (DESIGN.md §4.9): how much charge it holds, how fast it takes charge from its island, its
/// priority when the island runs short, and what it spends per tick just standing.
/// </summary>
/// <param name="Capacity">The most charge it holds.</param>
/// <param name="TransferPerTick">The most charge it takes from its island in one tick.</param>
/// <param name="Tier">Its priority in a shortage.</param>
/// <param name="IdleDrawPerTick">Charge it spends every tick, fed or cut off; it becomes heat where the machine stands.</param>
public readonly record struct MachineCapacitor(double Capacity, double TransferPerTick, PowerTier Tier, double IdleDrawPerTick);

/// <summary>The tank's build keys (<see cref="Input.ControllerOutput"/> primary through quinary).</summary>
public enum BuildKey { Primary, Secondary, Tertiary, Quaternary, Quinary }

/// <summary>
/// Defines per-type machine behavior and traits in one place: Core code asks the behavior, never the type.
/// Adding a new machine type = a <see cref="MachineType"/> value and one entry in <see cref="MachineBehaviors.All"/>.
/// </summary>
/// <param name="ActionInterval">Time between actions; <see cref="TimeSpan.Zero"/> acts on every world tick.</param>
/// <param name="OreCost">What building it costs, in ore (DESIGN.md §6.4).</param>
/// <param name="Key">The build key that builds it while the tank carries nothing.</param>
public record MachineBehavior(
    MachineType Type,
    TimeSpan ActionInterval,
    Action<Machine, MachineContext>? PerformAction,
    Color ActiveColor,
    int OreCost,
    BuildKey Key)
{
    /// <summary>Its capacitor in the energy network (DESIGN.md §4.9). Every machine has one.</summary>
    public MachineCapacitor Capacitor { get; init; }

    /// <summary>
    /// Its action spends charge, and it stops when its capacitor is empty (a harvester). Without the energy network,
    /// only such a machine keeps its capacitor, as the battery that pylons fill (DESIGN.md §4.5).
    /// </summary>
    public bool RunsOnCharge { get; init; }

    /// <summary>
    /// The battery it is built with (empty) when the energy network is off: its capacitor if it runs on charge, else
    /// none.
    /// </summary>
    public double BatteryCapacity => RunsOnCharge ? Capacitor.Capacity : 0.0;

    public bool HasBattery => BatteryCapacity > 0.0;

    /// <summary>
    /// Planted, it becomes a fixed node of the link network: it links to its base, is powered through it and, while
    /// powered, cools tanks around it (network coverage). A roaming machine is not a node.
    /// </summary>
    public bool IsLinkNode { get; init; }

    /// <summary>It works through the link network, so the tank refuses to plant it where it would not link.</summary>
    public bool RequiresLinkToPlant { get; init; }

    /// <summary>It charges batteries in its reach; a low battery heads for the nearest one.</summary>
    public bool IsCharger { get; init; }

    /// <summary>It mines ore into its base's store; its state is a <see cref="HarvesterState"/>.</summary>
    public bool IsMiner { get; init; }

    /// <summary>It pumps heat out of the ground into its base (<see cref="Machine.LastPumpedEnergy"/>).</summary>
    public bool IsHeatPump { get; init; }

    /// <summary>Converts local heat to charge and can supply an island without a base.</summary>
    public bool IsGenerator { get; init; }

    /// <summary>It fires at enemy tanks in its range while its island feeds it (<see cref="TurretAction"/>).</summary>
    public bool IsTurret { get; init; }

    /// <summary>It works only on the energy network, so only matches that run it can build it.</summary>
    public bool NeedsEnergyNetwork { get; init; }

    /// <summary>Whether a match with <paramref name="rules"/> can build it.</summary>
    public bool IsAvailable(MatchRules? rules) => !NeedsEnergyNetwork || rules is { EnergyNetwork: true };

    /// <summary>
    /// The paths it can be upgraded along (<see cref="MachineUpgradeSystem"/>), in the order of the upgrade keys 1, 2,
    /// 3; empty for a machine without upgrades.
    /// </summary>
    public ImmutableArray<MachineUpgradePath> UpgradePaths { get; init; } = [];
}

/// <summary>What a planted harvester is doing, as of its last action.</summary>
public enum HarvesterState
{
    Working,

    /// <summary>Heading back to a pylon to recharge.</summary>
    Returning,

    /// <summary>Its battery cannot pay for another harvest.</summary>
    OutOfCharge,

    /// <summary>Out of its base's network coverage: it cannot deliver, so it does not mine.</summary>
    OffNetwork,

    /// <summary>Stalled: the ground around it is too hot.</summary>
    TooHot,

    /// <summary>No ore in its work area that it can get within reach of: idle.</summary>
    NoOre,

    /// <summary>
    /// It can reach no plain ore and its work area holds a lump's warm core, which a harvester does not mine: idle
    /// until the tank torches it out.
    /// </summary>
    CoreOnly,

    /// <summary>It has ore to work but its base's store is full: idle until ore is spent.</summary>
    StoreFull,
}

public static class MachineBehaviors
{
    public static readonly MachineBehavior Harvester = new(
        MachineType.Harvester,
        TimeSpan.FromMilliseconds(Tweaks.Machine.HarvesterIntervalMs),
        CrawlAndHarvest,
        Tweaks.Colors.Harvester,
        Tweaks.Ore.HarvesterCost,
        BuildKey.Secondary)
    {
        Capacitor = new(Tweaks.Machine.HarvesterBatteryCapacity, Tweaks.Machine.HarvesterTransferPerTick,
            PowerTier.Economy, Tweaks.Machine.HarvesterIdleDraw),
        RunsOnCharge = true,
        IsMiner = true,
    };

    public static readonly MachineBehavior Pylon = new(
        MachineType.Pylon,
        TimeSpan.FromMilliseconds(Tweaks.Machine.PylonIntervalMs),
        ChargeBatteries,
        Tweaks.Colors.Pylon,
        Tweaks.Ore.PylonCost,
        BuildKey.Tertiary)
    {
        Capacitor = new(Tweaks.Machine.PylonCapacitor, Tweaks.Machine.PylonTransferPerTick,
            PowerTier.Economy, Tweaks.Machine.PylonIdleDraw),
        IsLinkNode = true,
        RequiresLinkToPlant = true,
        IsCharger = true,
    };

    public static readonly MachineBehavior Relay = new(
        MachineType.Relay,
        TimeSpan.Zero,
        PumpHeat,
        Tweaks.Colors.Relay,
        Tweaks.Ore.RelayCost,
        BuildKey.Primary)
    {
        Capacitor = new(Tweaks.Machine.RelayCapacitor, Tweaks.Machine.RelayTransferPerTick,
            PowerTier.Economy, Tweaks.Machine.RelayIdleDraw),
        IsLinkNode = true,
        RequiresLinkToPlant = true,
        IsHeatPump = true,
        UpgradePaths = [MachineUpgradePath.Tap, MachineUpgradePath.Sensor, MachineUpgradePath.Conduit],
    };

    public static readonly MachineBehavior Generator = new(
        MachineType.Generator, TimeSpan.Zero,
        static (machine, ctx) => ctx.Energy?.Generate(machine, ctx.Terrain),
        Tweaks.Colors.Generator, Tweaks.Ore.GeneratorCost, BuildKey.Quaternary)
    {
        Capacitor = new(Tweaks.Machine.GeneratorCapacitor, Tweaks.Machine.GeneratorTransferPerTick,
            PowerTier.Economy, Tweaks.Machine.GeneratorIdleDraw),
        IsLinkNode = true,
        IsGenerator = true,
        NeedsEnergyNetwork = true,
    };

    public static readonly MachineBehavior Turret = new(
        MachineType.Turret, TimeSpan.Zero, TurretAction.Act,
        Tweaks.Turret.Color, Tweaks.Turret.OreCost, BuildKey.Quinary)
    {
        Capacitor = new(Tweaks.Turret.Capacitor, Tweaks.Turret.TransferPerTick, PowerTier.Turrets, Tweaks.Turret.IdleDraw),
        IsTurret = true,
        NeedsEnergyNetwork = true,
        UpgradePaths = [MachineUpgradePath.Rapid, MachineUpgradePath.LongRange],
    };

    /// <summary>Every machine's behavior, indexed by <see cref="MachineType"/>.</summary>
    public static readonly IReadOnlyList<MachineBehavior> All = BuildTable(Harvester, Pylon, Relay, Generator, Turret);

    public static MachineBehavior Get(MachineType type) =>
        (uint)type < (uint)All.Count ? All[(int)type] : throw new ArgumentOutOfRangeException(nameof(type));

    /// <summary>The machine <paramref name="key"/> builds.</summary>
    public static MachineBehavior ForKey(BuildKey key)
    {
        foreach (var behavior in All)
            if (behavior.Key == key)
                return behavior;
        throw new ArgumentOutOfRangeException(nameof(key));
    }

    private static MachineBehavior[] BuildTable(params MachineBehavior[] behaviors)
    {
        var table = new MachineBehavior[Enum.GetValues<MachineType>().Length];
        foreach (var behavior in behaviors)
            table[(int)behavior.Type] = behavior;
        if (Array.IndexOf(table, null) >= 0)
            throw new InvalidOperationException("Every MachineType needs a MachineBehavior.");
        return table;
    }

    // Scratch for one pylon action. Per thread, because worlds on different threads (tests, replays) may act at the
    // same time; nothing in it outlives a call.
    [ThreadStatic]
    private static List<Machine>? t_near;

    /// <summary>The ground temperature a harvester at <paramref name="at"/> senses (averaged over its sense radius).</summary>
    public static float HarvesterSenseTemperature(ITerrainHeatSource heat, Position at) =>
        // SampleAverageHeat is normalised (temperature / 255).
        heat.SampleAverageHeat(at, Tweaks.Machine.HarvesterHeatSenseRadius) * 255f;

    /// <summary>A sensed ground temperature as a fraction of the harvester's stall temperature (1 = stalled).</summary>
    public static float HarvesterSenseHeat(float temperature) => temperature / Tweaks.Machine.HarvesterStallTemperature;

    /// <summary>True when a harvester's battery cannot pay for another harvest.</summary>
    public static bool IsOutOfCharge(Machine harvester) => harvester.Charge < Tweaks.Machine.HarvesterHeatPerHarvest;

    /// <summary>
    /// A harvester's state, each harvester in exactly one, in this order: heading to a pylon, out of charge, off the
    /// network, too hot, out of ore (or left only a core), held up by a full store, otherwise working.
    /// </summary>
    public static HarvesterState HarvesterStateOf(Machine harvester, bool baseDark = false) =>
        baseDark ? HarvesterState.OffNetwork
        : harvester.IsReturning ? HarvesterState.Returning
        : IsOutOfCharge(harvester) ? HarvesterState.OutOfCharge
        : harvester.IsOffNetwork ? HarvesterState.OffNetwork
        : harvester.IsStalled ? HarvesterState.TooHot
        : harvester.HasOnlyCoreOre ? HarvesterState.CoreOnly
        : harvester.HasNoOre ? HarvesterState.NoOre
        : harvester.OwnerBase is { Store.IsFull: true } ? HarvesterState.StoreFull
        : HarvesterState.Working;

    /// <summary>
    /// Heat pipe: when the relay is connected to a powered link network it pulls heat out of the area around it
    /// and stores it in its owner's base reactor (which vents it at the radiator rate).
    /// </summary>
    private static void PumpHeat(Machine machine, MachineContext ctx)
    {
        var terrain = ctx.Terrain;
        machine.LastPumpedEnergy = 0.0;
        machine.LastChargeMade = 0.0;
        machine.LastPumpLimitedByBase = false;
        if (machine.OwnerBase == null || machine.LinkPoint is not { IsPowered: true })
            return;

        double rate = ctx.Upgrades?.PumpPerTick(machine) ?? Tweaks.Machine.RelayHeatPumpPerTick;
        // Ground that had no heat to give has none until heat moves there again: skip the sweep until then.
        long quiet = terrain.QuietHeatStamp(machine.Position, Tweaks.Machine.RelayCoolingRadius);
        if (quiet >= 0 && quiet == machine.ColdGroundStamp)
        {
            machine.LastPumpLimitedByBase = machine.OwnerBase.LimitsPumpFromColdGround(rate);
            return;
        }

        machine.LastPumpedEnergy = machine.OwnerBase.AbsorbHeatFrom(
            terrain, machine.Position, Tweaks.Machine.RelayCoolingRadius, rate,
            out bool limited, out double engineOutput, out bool groundWasCold);
        machine.ColdGroundStamp = groundWasCold ? quiet : -1;
        machine.LastPumpLimitedByBase = limited;
        machine.LastChargeMade = engineOutput;
        LinkMap.CarryHeat(machine.LinkPoint, machine.LastPumpedEnergy);
    }

    /// <summary>
    /// A roaming harvester (DESIGN.md §6.4): it works the ore within <c>Ore.HarvesterReach</c> of itself into its owner
    /// base's store, <c>HarvesterPlainWork</c> actions a cell; with no ore in reach it crawls one cell along the
    /// shortest path its footprint can take to ore within <c>HarvestRange</c> of where it was planted
    /// (<see cref="HarvesterPathing.FindReachableOre"/>), clearing the dirt in its way (worth nothing). Ore it cannot
    /// get within reach of (sealed in rock, say) it ignores, and so it does ore that is too hot to work
    /// (<c>HarvesterHotOreTemperature</c>: the shell of a lump while its core is in, which it leaves to the tank's
    /// torch, DESIGN.md §6.3), going for other ore instead.
    /// It works only inside its base's network coverage, which is how its ore gets home, and while the ground around it
    /// is cool; off the network or on hot ground it still crawls toward ore but works none. It idles, spending
    /// nothing, with no ore left or while the store is full. Working spends
    /// battery charge, which ends up as heat in the ground, and it stalls while that ground is too hot, so it needs a
    /// relay nearby to keep going. When its battery runs low it crawls back toward the nearest powered pylon of its
    /// base and waits in its beam until it is charged again.
    /// </summary>
    private static void CrawlAndHarvest(Machine machine, MachineContext ctx)
    {
        var terrain = ctx.Terrain;

        float temperature = HarvesterSenseTemperature(terrain, machine.Position);
        machine.SenseHeat = HarvesterSenseHeat(temperature);
        machine.IsStalled = temperature >= Tweaks.Machine.HarvesterStallTemperature;

        if (machine.OwnerBase is not { IsDestroyed: false } home)
            return;
        var store = home.Store;

        // On the energy network the capacitor is brought up to date first, and there are no refuel trips: the island
        // feeds it wherever its base's network covers it.
        ctx.Energy?.Settle(machine, terrain);
        if (ctx.BasePower?.IsDark(home) == true)
            return;
        if (ctx.Energy == null && ReturnToPylon(machine, ctx))
            return;

        var coverage = ctx.CoverageOf(machine);
        machine.IsOffNetwork = !coverage.Covered;
        ctx.Energy?.SetCovered(machine, coverage, ctx.Machines, terrain, t_near ??= new List<Machine>());
        // With a flat battery there is nothing to work with. Off the network its ore could not get home, and on ground
        // that is too hot it waits for the ground to cool (a linked relay nearby speeds that up a lot): either way it
        // does not work, but it still crawls on toward ore it could work, so that a tunnel that bends out of its relay's
        // sight, or hot ground beside a lump, never leaves it frozen where it stands. Each of these is a state of its
        // own that outranks the ore flags (HarvesterStateOf), so they may go stale behind it.
        if (IsOutOfCharge(machine))
            return;
        bool canWork = !machine.IsOffNetwork && !machine.IsStalled;

        var reachable = HarvesterPathing.FindReachableOre(machine, ctx, out Offset step, out bool coreLeft);
        machine.HasNoOre = reachable == null;
        machine.HasOnlyCoreOre = reachable == null && coreLeft;
        if (reachable is not Position ore)
        {
            ForgetOreWork(machine);
            return;
        }
        // With the store full there is nowhere to put the ore: it waits where it is (HarvesterState.StoreFull).
        if (store.IsFull)
            return;

        if (step == default)
        {
            if (!canWork)
                return;
            WorkOre(machine, ctx, ore, store);
            PayForWork(machine, ctx, worked: true, moved: false);
            return;
        }
        machine.Position += step;
        machine.Heading = step;
        CarvePath(machine, terrain);
        PayForWork(machine, ctx, worked: false, moved: true);
    }

    /// <summary>
    /// One action of work on the plain ore cell at <paramref name="at"/>: once the cell has had the work it takes, its units
    /// go to <paramref name="store"/> and the cell is cleared (<see cref="StepEventKind.OreHarvested"/>). A cell whose
    /// units do not all fit stays in the ground.
    /// </summary>
    private static void WorkOre(Machine machine, MachineContext ctx, Position at, OreStore store)
    {
        var terrain = ctx.Terrain;
        // Work belongs to one cell: if the cell it was on is gone (the tank or another harvester took it) or is no
        // longer the nearest, it starts this one from nothing.
        if (machine.OreWorkCell != at)
        {
            machine.OreWorkCell = at;
            machine.OreWork = 0;
        }
        machine.OreWork++;
        int units = Pixel.OreUnits(terrain.GetPixelRaw(at));
        if (machine.OreWork < Tweaks.Ore.HarvesterPlainWork || store.Room < units)
            return;
        terrain.SetPixel(at, TerrainPixel.Blank);
        store.Add(units);
        ctx.Events?.Record(new StepEvent(StepEventKind.OreHarvested, at, machine.OwnerColor, machine.Type, Count: units));
        ForgetOreWork(machine);
    }

    private static void ForgetOreWork(Machine machine)
    {
        machine.OreWork = 0;
        machine.OreWorkCell = null;
    }

    /// <summary>
    /// The low-battery half of a harvester: from below <c>HarvesterReturnBelow</c> until above
    /// <c>HarvesterResumeAbove</c> it heads for the nearest powered pylon of its base and waits once it is in that
    /// pylon's beam. With no pylon to go to it keeps working until the battery is flat. True when it used the action.
    /// </summary>
    private static bool ReturnToPylon(Machine machine, MachineContext ctx)
    {
        if (machine.ChargeFraction < Tweaks.Machine.HarvesterReturnBelow)
            machine.IsReturning = true;
        else if (machine.ChargeFraction >= Tweaks.Machine.HarvesterResumeAbove)
            machine.IsReturning = false;
        if (!machine.IsReturning)
            return false;

        var pylon = NearestPylon(machine, ctx);
        if (pylon == null)
        {
            machine.IsReturning = false;
            return false;
        }
        if (CanCharge(pylon, machine, ctx.Terrain))
            return true; // in the beam: wait

        if (machine.Charge < Tweaks.Machine.HarvesterCrawlCharge)
            return true; // flat: stranded until a pylon reaches it
        bool moved = Crawl(machine, ctx, pylon.Position);
        // Its way home is mostly its own tunnel, but it may cut through dirt (or regrowth) outside its work area: carve
        // it open, so the pylon can see it once it arrives.
        bool carved = moved && CarvePath(machine, ctx.Terrain);
        PayForWork(machine, ctx, worked: false, moved || carved);
        return true;
    }

    /// <summary>
    /// Clears every dirt cell, settled or regrowing, within the mine radius, ignoring the work area. True when it
    /// cleared any. Dirt is worth nothing; this only keeps the harvester's tunnel open.
    /// </summary>
    private static bool CarvePath(Machine machine, ITerrainAccess terrain)
    {
        int r = Tweaks.Machine.HarvesterMineRadius;
        bool carved = false;
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (dx * dx + dy * dy > r * r) continue;
                var pos = new Position(machine.Position.X + dx, machine.Position.Y + dy);
                if (!terrain.IsInside(pos)) continue;
                var pixel = terrain.GetPixelRaw(pos);
                if (pixel != TerrainPixel.Dirt && pixel != TerrainPixel.DirtGrow) continue;
                terrain.SetPixel(pos, TerrainPixel.Blank);
                carved = true;
            }
        return carved;
    }

    /// <summary>
    /// Books the charge one action took: working ore spends <c>HarvesterHeatPerHarvest</c>, a crawl step
    /// <c>HarvesterCrawlCharge</c>. The charge becomes heat in the ground beside the harvester (booked in the ledger as
    /// charge turned back into heat). On the energy network it is spent through the network, which refills it.
    /// </summary>
    private static void PayForWork(Machine machine, MachineContext ctx, bool worked, bool moved)
    {
        double cost = worked ? Tweaks.Machine.HarvesterHeatPerHarvest : moved ? Tweaks.Machine.HarvesterCrawlCharge : 0.0;
        if (ctx.Energy != null)
        {
            ctx.Energy.Spend(machine, cost, ctx.Terrain);
            return;
        }
        cost = Math.Min(cost, machine.Charge);
        if (cost <= 0.0)
            return;
        machine.DischargeAsHeat(ctx.Terrain, cost);
    }

    private static bool Crawl(Machine machine, MachineContext ctx, Position target)
    {
        Offset step = HarvesterPathing.ChooseStep(machine, ctx, target);
        if (step == default)
            return false;
        machine.Position += step;
        machine.Heading = step;
        return true;
    }

    /// <summary>The closest powered pylon of the harvester's own base (first in list order on ties), or null.</summary>
    private static Machine? NearestPylon(Machine machine, MachineContext ctx)
    {
        Machine? best = null;
        int bestDist = int.MaxValue;
        foreach (var other in ctx.Machines.Chargers)
        {
            if (!other.IsStanding ||
                other.OwnerBase != machine.OwnerBase || other.LinkPoint is not { IsPowered: true })
                continue;
            int d = Position.DistanceSquared(other.Position, machine.Position);
            if (d < bestDist)
            {
                bestDist = d;
                best = other;
            }
        }
        return best;
    }

    /// <summary>A pylon reaches a battery within <c>PylonRange</c> in direct line of sight through open ground.</summary>
    public static bool CanCharge(Machine pylon, Machine battery, ITerrainPixelReadOnly terrain)
    {
        int range = Tweaks.Machine.PylonRange;
        if (Position.DistanceSquared(pylon.Position, battery.Position) > range * range)
            return false;
        return Collision.Raycaster.HasOpenLine(terrain, pylon.Position, battery.Position);
    }

    /// <summary>
    /// Pylon: while its link network reaches its base, it hands up to <c>PylonChargePerAction</c> of the base's charge
    /// to the batteries of its base's machines that it can reach (<see cref="CanCharge"/>), in list order.
    /// </summary>
    private static void ChargeBatteries(Machine pylon, MachineContext ctx)
    {
        pylon.LastChargeGiven = 0.0;
        // Only this pylon ever names itself as a battery's charger, and only below.
        foreach (var m in pylon.ChargedAtLastAction)
            if (ReferenceEquals(m.ChargingFrom, pylon))
                m.ChargingFrom = null;
        pylon.ChargedAtLastAction.Clear();

        // On the energy network every machine draws from its island itself; a pylon hands out nothing.
        var home = pylon.OwnerBase;
        if (home == null || pylon.LinkPoint is not { IsPowered: true } || ctx.Energy != null)
            return;

        double budget = Tweaks.Machine.PylonChargePerAction;
        // The batteries in range, in list order: which ones get the budget when it runs short depends on it.
        var near = t_near ??= new List<Machine>();
        ctx.Machines.CollectNear(pylon.Position, Tweaks.Machine.PylonRange, near);
        foreach (var m in near)
        {
            if (budget <= 0.0 || home.Charge <= 0.0)
                break;
            if (m.ChargeCapacity <= 0.0 || !m.StandsFor(home) ||
                m.Charge >= m.ChargeCapacity || !CanCharge(pylon, m, ctx.Terrain))
                continue;
            double given = home.DrawCharge(Math.Min(budget, m.ChargeCapacity - m.Charge));
            m.Charge += given;
            m.ChargingFrom = pylon;
            pylon.ChargedAtLastAction.Add(m);
            budget -= given;
            pylon.LastChargeGiven += given;
        }
    }
}
