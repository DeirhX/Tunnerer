namespace Tunnerer.Core.Entities.Machines;

using Tunnerer.Core.Types;
using Tunnerer.Core.Config;
using Tunnerer.Core.Resources;
using Tunnerer.Core.Terrain;

/// <summary>What a machine's action can see and touch.</summary>
/// <param name="IsOccupied">
/// True when the position would put the given machine on top of a tank or another machine; a roaming harvester
/// checks it before every step.
/// </param>
/// <param name="Machines">Every machine in the world, in a fixed order: pylons look for batteries, harvesters for pylons.</param>
/// <param name="IsOnNetwork">
/// True when the machine stands in the coverage of its own base's linked network (DESIGN.md §4.6): a harvester only
/// delivers ore from there.
/// </param>
public readonly record struct MachineContext(
    ITerrainAccess Terrain, Func<Machine, Position, bool> IsOccupied, IReadOnlyList<Machine> Machines,
    Func<Machine, bool> IsOnNetwork);

/// <summary>The tank's build keys (<see cref="Input.ControllerOutput"/> BuildPrimary, BuildSecondary, BuildTertiary).</summary>
public enum BuildKey { Primary, Secondary, Tertiary }

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
    /// <summary>Battery size it is built with (empty); zero for machines without a battery.</summary>
    public double BatteryCapacity { get; init; }

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

    /// <summary>No ore left in its work area: idle.</summary>
    NoOre,

    /// <summary>
    /// The only ore left in its work area is a lump's warm core, which a harvester does not mine: idle until the tank
    /// torches it out.
    /// </summary>
    CoreOnly,
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
        BatteryCapacity = Tweaks.Machine.HarvesterBatteryCapacity,
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
        IsLinkNode = true,
        RequiresLinkToPlant = true,
        IsHeatPump = true,
    };

    /// <summary>Every machine's behavior, indexed by <see cref="MachineType"/>.</summary>
    public static readonly IReadOnlyList<MachineBehavior> All = BuildTable(Harvester, Pylon, Relay);

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
    /// network, too hot, out of ore (or left only a core), otherwise working.
    /// </summary>
    public static HarvesterState HarvesterStateOf(Machine harvester) =>
        harvester.IsReturning ? HarvesterState.Returning
        : IsOutOfCharge(harvester) ? HarvesterState.OutOfCharge
        : harvester.IsOffNetwork ? HarvesterState.OffNetwork
        : harvester.IsStalled ? HarvesterState.TooHot
        : harvester.HasOnlyCoreOre ? HarvesterState.CoreOnly
        : harvester.HasNoOre ? HarvesterState.NoOre
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

        machine.LastPumpedEnergy = machine.OwnerBase.AbsorbHeatFrom(
            terrain, machine.Position, Tweaks.Machine.RelayCoolingRadius, Tweaks.Machine.RelayHeatPumpPerTick,
            out bool limited, out double engineOutput);
        machine.LastPumpLimitedByBase = limited;
        machine.LastChargeMade = engineOutput;
        Links.LinkMap.CarryHeat(machine.LinkPoint, machine.LastPumpedEnergy);
    }

    /// <summary>
    /// A roaming harvester (DESIGN.md §6.4): it works the ore within <c>Ore.HarvesterReach</c> of itself into its owner
    /// base's store, <c>HarvesterPlainWork</c> actions a cell; with no ore in reach
    /// it crawls one cell toward the nearest ore within <c>HarvestRange</c> of where it was planted, clearing the dirt
    /// in its way (worth nothing). It leaves a lump's warm core to the tank's torch (DESIGN.md §6.3), and the core's
    /// heat keeps it off the ore around until the core is gone and the ground has cooled.
    /// It works only inside its base's network coverage, which is how its ore gets home,
    /// and idles, spending nothing, off the network, with no ore left, or while the store is full. Working spends
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

        var store = machine.OwnerBase?.Store;
        if (store == null)
            return;

        if (ReturnToPylon(machine, ctx))
            return;

        machine.IsOffNetwork = !ctx.IsOnNetwork(machine);
        // Off the network its ore could not get home; too hot, it waits for the ground to cool (a linked relay nearby
        // speeds that up a lot); with the store full there is nowhere to put the ore; with a flat battery there is
        // nothing to work with.
        if (machine.IsOffNetwork || machine.IsStalled || store.IsFull || IsOutOfCharge(machine))
            return;

        var nearest = FindNearestOre(machine, terrain, out bool coreLeft);
        machine.HasNoOre = nearest == null;
        machine.HasOnlyCoreOre = nearest == null && coreLeft;
        if (nearest is not Position ore)
        {
            ForgetOreWork(machine);
            return;
        }

        int reach = Tweaks.Ore.HarvesterReach;
        if (Position.DistanceSquared(ore, machine.Position) <= reach * reach)
        {
            WorkOre(machine, terrain, ore, store);
            PayForWork(machine, terrain, worked: true, moved: false);
            return;
        }
        bool moved = Crawl(machine, ctx, ore, confined: true);
        if (moved)
            CarvePath(machine, terrain);
        PayForWork(machine, terrain, worked: false, moved);
    }

    /// <summary>
    /// One action of work on the plain ore cell at <paramref name="at"/>: once the cell has had the work it takes, its units
    /// go to <paramref name="store"/> and the cell is cleared. A cell whose units do not all fit stays in the ground.
    /// </summary>
    private static void WorkOre(Machine machine, ITerrainAccess terrain, Position at, OreStore store)
    {
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
        bool moved = Crawl(machine, ctx, pylon.Position, confined: false);
        // Its way home is mostly its own tunnel, but it may cut through dirt (or regrowth) outside its work area: carve
        // it open, so the pylon can see it once it arrives.
        bool carved = moved && CarvePath(machine, ctx.Terrain);
        PayForWork(machine, ctx.Terrain, worked: false, moved || carved);
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
    /// charge turned back into heat).
    /// </summary>
    private static void PayForWork(Machine machine, ITerrainAccess terrain, bool worked, bool moved)
    {
        double cost = worked ? Tweaks.Machine.HarvesterHeatPerHarvest : moved ? Tweaks.Machine.HarvesterCrawlCharge : 0.0;
        cost = Math.Min(cost, machine.Charge);
        if (cost <= 0.0)
            return;
        double heat = terrain.AddEnergyInRadiusArea(machine.Position, Tweaks.Machine.HarvesterHeatRadius,
            cost, TerrainGrid.ThermalField.Terrain);
        machine.Charge -= heat;
        terrain.Ledger.RecordFromCharge(heat);
    }

    private static bool Crawl(Machine machine, MachineContext ctx, Position target, bool confined)
    {
        Offset step = ChooseStep(machine, ctx, target, confined);
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
        foreach (var other in ctx.Machines)
        {
            if (!other.Behavior.IsCharger || !other.IsAlive || other.State != MachineState.Planted ||
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
        foreach (var m in ctx.Machines)
            if (ReferenceEquals(m.ChargingFrom, pylon))
                m.ChargingFrom = null;

        var home = pylon.OwnerBase;
        if (home == null || pylon.LinkPoint is not { IsPowered: true })
            return;

        double budget = Tweaks.Machine.PylonChargePerAction;
        foreach (var m in ctx.Machines)
        {
            if (budget <= 0.0 || home.Charge <= 0.0)
                break;
            if (m.ChargeCapacity <= 0.0 || !m.IsAlive || m.State != MachineState.Planted || m.OwnerBase != home ||
                m.Charge >= m.ChargeCapacity || !CanCharge(pylon, m, ctx.Terrain))
                continue;
            double given = home.DrawCharge(Math.Min(budget, m.ChargeCapacity - m.Charge));
            m.Charge += given;
            m.ChargingFrom = pylon;
            budget -= given;
            pylon.LastChargeGiven += given;
        }
    }

    private static bool InWorkArea(Machine machine, Position p)
    {
        int range = Tweaks.Machine.HarvestRange;
        return Position.DistanceSquared(p, machine.Anchor) <= range * range;
    }

    /// <summary>
    /// The plain ore cell in the work area closest to the harvester (first in scan order on ties), or null;
    /// <paramref name="coreLeft"/> says whether the area still holds core ore, which harvesters leave to the tank.
    /// </summary>
    private static Position? FindNearestOre(Machine machine, ITerrainAccess terrain, out bool coreLeft)
    {
        coreLeft = false;
        int range = Tweaks.Machine.HarvestRange;
        Position? best = null;
        int bestDist = int.MaxValue;
        for (int dy = -range; dy <= range; dy++)
            for (int dx = -range; dx <= range; dx++)
            {
                if (dx * dx + dy * dy > range * range) continue;
                var pos = new Position(machine.Anchor.X + dx, machine.Anchor.Y + dy);
                if (!terrain.IsInside(pos)) continue;
                var pixel = terrain.GetPixelRaw(pos);
                if (Pixel.IsOreCore(pixel))
                {
                    coreLeft = true;
                    continue;
                }
                if (!Pixel.IsOre(pixel)) continue;
                int d = Position.DistanceSquared(pos, machine.Position);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = pos;
                }
            }
        return best;
    }

    // The eight crawl directions, in a fixed order so ties resolve the same way every run.
    private static readonly Offset[] Steps =
    [
        new(1, 0), new(1, 1), new(0, 1), new(-1, 1), new(-1, 0), new(-1, -1), new(0, -1), new(1, -1),
    ];

    /// <summary>
    /// The step that brings the harvester closest to <paramref name="target"/>, preferring its current heading on
    /// ties and never straight back, through ground its footprint can cross (<see cref="IsCrawlable"/>: not rock,
    /// concrete or base) and not onto a tank or another machine. Zero when it is boxed in. <paramref name="confined"/> keeps it in
    /// its work area (a harvester that left it to recharge may always step back toward its anchor).
    /// </summary>
    private static Offset ChooseStep(Machine machine, MachineContext ctx, Position target, bool confined)
    {
        Offset best = default;
        int bestScore = int.MaxValue;
        var back = new Offset(-machine.Heading.X, -machine.Heading.Y);
        // Planted right beside a relay or tank it already overlaps: let it crawl out rather than freeze.
        bool alreadyCrowded = ctx.IsOccupied(machine, machine.Position);
        int anchorDist = Position.DistanceSquared(machine.Position, machine.Anchor);
        foreach (var step in Steps)
        {
            if (machine.Heading != default && step == back) continue;
            var next = machine.Position + step;
            bool allowed = !confined || InWorkArea(machine, next) ||
                Position.DistanceSquared(next, machine.Anchor) < anchorDist;
            if (!allowed || !CanStand(machine, ctx.Terrain, next) ||
                (!alreadyCrowded && ctx.IsOccupied(machine, next)))
                continue;
            int score = Position.DistanceSquared(next, target) * 2 + (step == machine.Heading ? 0 : 1);
            if (score < bestScore)
            {
                bestScore = score;
                best = step;
            }
        }
        return best;
    }

    private static bool CanStand(Machine machine, ITerrainAccess terrain, Position center)
    {
        int half = machine.BoundingBox.HalfWidth;
        for (int dy = -half; dy <= half; dy++)
            for (int dx = -half; dx <= half; dx++)
            {
                var pos = new Position(center.X + dx, center.Y + dy);
                if (!terrain.IsInside(pos)) return false;
                if (!IsCrawlable(terrain.GetPixelRaw(pos))) return false;
            }
        return true;
    }

    /// <summary>
    /// Ground a harvester's footprint may cross: anything that does not block movement (open, settled or regrowing
    /// dirt, scorch, gold) except a base.
    /// </summary>
    private static bool IsCrawlable(TerrainPixel pixel) =>
        !Pixel.IsBlockingCollision(pixel) && !Pixel.IsBase(pixel) &&
        pixel is not (TerrainPixel.BaseBarrier or TerrainPixel.BaseCore);
}
