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
public readonly record struct MachineContext(
    ITerrainAccess Terrain, Func<Machine, Position, bool> IsOccupied, IReadOnlyList<Machine> Machines);

/// <summary>The tank's build keys (<see cref="Input.ControllerOutput"/> BuildPrimary, BuildSecondary, BuildTertiary).</summary>
public enum BuildKey { Primary, Secondary, Tertiary }

/// <summary>
/// Defines per-type machine behavior and traits in one place: Core code asks the behavior, never the type.
/// Adding a new machine type = a <see cref="MachineType"/> value and one entry in <see cref="MachineBehaviors.All"/>.
/// </summary>
/// <param name="ActionInterval">Time between actions; <see cref="TimeSpan.Zero"/> acts on every world tick.</param>
/// <param name="Key">The build key that builds it while the tank carries nothing.</param>
public record MachineBehavior(
    MachineType Type,
    TimeSpan ActionInterval,
    Action<Machine, MachineContext>? PerformAction,
    Color ActiveColor,
    MaterialAmount BuildCost,
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

    /// <summary>It mines dirt into its base's store; its state is a <see cref="HarvesterState"/>.</summary>
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

    /// <summary>Stalled: the ground around it is too hot.</summary>
    TooHot,
}

public static class MachineBehaviors
{
    public static readonly MachineBehavior Harvester = new(
        MachineType.Harvester,
        TimeSpan.FromMilliseconds(Tweaks.Machine.HarvesterIntervalMs),
        CrawlAndHarvest,
        Tweaks.Colors.Harvester,
        new MaterialAmount(Tweaks.Machine.HarvesterDirtCost, 0),
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
        new MaterialAmount(Tweaks.Machine.PylonDirtCost, 0),
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
        new MaterialAmount(Tweaks.Machine.RelayDirtCost, 0),
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
    /// A harvester's state, each harvester in exactly one, in this order: heading to a pylon, out of charge, too hot,
    /// otherwise working.
    /// </summary>
    public static HarvesterState HarvesterStateOf(Machine harvester) =>
        harvester.IsReturning ? HarvesterState.Returning
        : IsOutOfCharge(harvester) ? HarvesterState.OutOfCharge
        : harvester.IsStalled ? HarvesterState.TooHot
        : HarvesterState.Working;

    /// <summary>
    /// Heat pipe: when the relay is connected to a powered link network it pulls heat out of the area around it
    /// and stores it in its owner's base reactor (which vents it at the radiator rate).
    /// </summary>
    private static void PumpHeat(Machine machine, MachineContext ctx)
    {
        var terrain = ctx.Terrain;
        machine.LastPumpedEnergy = 0.0;
        machine.LastPumpLimitedByBase = false;
        if (machine.OwnerBase == null || machine.LinkPoint is not { IsPowered: true })
            return;

        machine.LastPumpedEnergy = machine.OwnerBase.AbsorbHeatFrom(
            terrain, machine.Position, Tweaks.Machine.RelayCoolingRadius, Tweaks.Machine.RelayHeatPumpPerTick,
            out bool limited);
        machine.LastPumpLimitedByBase = limited;
        Links.LinkMap.CarryHeat(machine.LinkPoint, machine.LastPumpedEnergy);
    }

    /// <summary>
    /// A roaming harvester: each action it eats the settled dirt within <c>HarvesterMineRadius</c> of itself into its
    /// owner base's store; when nothing is left in reach it crawls one cell toward the nearest dirt within
    /// <c>HarvestRange</c> of where it was planted, carving its own tunnel. Working spends battery charge, which ends
    /// up as heat in the ground, and it stalls while that ground is too hot, so it needs a relay nearby to keep going.
    /// It idles while the store is full. When its battery runs low it crawls back toward the nearest powered pylon of
    /// its base and waits in its beam until it is charged again.
    /// </summary>
    private static void CrawlAndHarvest(Machine machine, MachineContext ctx)
    {
        var terrain = ctx.Terrain;

        float temperature = HarvesterSenseTemperature(terrain, machine.Position);
        machine.SenseHeat = HarvesterSenseHeat(temperature);
        machine.IsStalled = temperature >= Tweaks.Machine.HarvesterStallTemperature;

        var store = machine.OwnerBase?.Materials;
        if (store == null)
            return;
        int room = Math.Max(0, store.DirtCapacity - store.Dirt);

        if (ReturnToPylon(machine, ctx, store, room))
            return;

        // Too hot to work: wait for the ground to cool (a linked relay nearby speeds that up a lot).
        if (machine.IsStalled)
            return;
        // With the store full there is nowhere to put the dirt; with a flat battery there is nothing to dig with.
        if (room <= 0 || IsOutOfCharge(machine))
            return;

        int mined = MineAround(machine, terrain, room);
        bool moved = false;
        if (mined == 0 && FindNearestDirt(machine, terrain) is Position target)
        {
            moved = Crawl(machine, ctx, target, confined: true);
            if (moved)
                mined = MineAround(machine, terrain, room);
        }
        PayForWork(machine, terrain, store, mined, moved);
    }

    /// <summary>
    /// The low-battery half of a harvester: from below <c>HarvesterReturnBelow</c> until above
    /// <c>HarvesterResumeAbove</c> it heads for the nearest powered pylon of its base and waits once it is in that
    /// pylon's beam. With no pylon to go to it keeps working until the battery is flat. True when it used the action.
    /// </summary>
    private static bool ReturnToPylon(Machine machine, MachineContext ctx, MaterialContainer store, int room)
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
        // it open, so the pylon can see it once it arrives. Settled dirt still goes to the store while there is room.
        int carved = moved ? CarvePath(machine, ctx.Terrain) : 0;
        PayForWork(machine, ctx.Terrain, store, Math.Min(carved, room), moved || carved > 0);
        return true;
    }

    /// <summary>
    /// Clears every dirt cell, settled or regrowing, within the mine radius, ignoring the work area. Returns how many
    /// settled cells it cleared (regrowing dirt is worth nothing).
    /// </summary>
    private static int CarvePath(Machine machine, ITerrainAccess terrain)
    {
        int r = Tweaks.Machine.HarvesterMineRadius;
        int settled = 0;
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (dx * dx + dy * dy > r * r) continue;
                var pos = new Position(machine.Position.X + dx, machine.Position.Y + dy);
                if (!terrain.IsInside(pos)) continue;
                var pixel = terrain.GetPixelRaw(pos);
                if (pixel != TerrainPixel.Dirt && pixel != TerrainPixel.DirtGrow) continue;
                terrain.SetPixel(pos, TerrainPixel.Blank);
                if (pixel == TerrainPixel.Dirt)
                    settled++;
            }
        return settled;
    }

    /// <summary>
    /// Books one action's results: mined dirt goes to the store, and the charge the work took becomes heat in the
    /// ground beside the harvester (booked in the ledger as charge turned back into heat).
    /// </summary>
    private static void PayForWork(Machine machine, ITerrainAccess terrain, MaterialContainer store, int mined, bool moved)
    {
        if (mined > 0)
            store.Add(new MaterialAmount(mined, 0));
        double cost = mined > 0 ? Tweaks.Machine.HarvesterHeatPerHarvest : moved ? Tweaks.Machine.HarvesterCrawlCharge : 0.0;
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

    /// <summary>Clears settled dirt (regrowing dirt is worth nothing) within the mine radius, inside the work area.</summary>
    private static int MineAround(Machine machine, ITerrainAccess terrain, int quota)
    {
        int r = Tweaks.Machine.HarvesterMineRadius;
        int mined = 0;
        for (int dy = -r; dy <= r && mined < quota; dy++)
            for (int dx = -r; dx <= r && mined < quota; dx++)
            {
                if (dx * dx + dy * dy > r * r) continue;
                var pos = new Position(machine.Position.X + dx, machine.Position.Y + dy);
                if (!terrain.IsInside(pos) || !InWorkArea(machine, pos)) continue;
                if (terrain.GetPixelRaw(pos) != TerrainPixel.Dirt) continue;
                terrain.SetPixel(pos, TerrainPixel.Blank);
                mined++;
            }
        return mined;
    }

    /// <summary>The settled dirt cell in the work area closest to the harvester (first in scan order on ties).</summary>
    private static Position? FindNearestDirt(Machine machine, ITerrainAccess terrain)
    {
        int range = Tweaks.Machine.HarvestRange;
        Position? best = null;
        int bestDist = int.MaxValue;
        for (int dy = -range; dy <= range; dy++)
            for (int dx = -range; dx <= range; dx++)
            {
                if (dx * dx + dy * dy > range * range) continue;
                var pos = new Position(machine.Anchor.X + dx, machine.Anchor.Y + dy);
                if (!terrain.IsInside(pos) || terrain.GetPixelRaw(pos) != TerrainPixel.Dirt) continue;
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
