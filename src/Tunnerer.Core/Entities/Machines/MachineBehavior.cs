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
    ITerrainAccess Terrain, Func<Machine, Position, bool> IsOccupied, IReadOnlyList<Machine> Machines)
{
    public static MachineContext ForTerrain(ITerrainAccess terrain) => new(terrain, static (_, _) => false, []);
}

/// <summary>
/// Defines per-type machine behavior in one place.
/// Adding a new machine type = one entry in <see cref="MachineBehaviors.Get"/>.
/// </summary>
public record MachineBehavior(
    TimeSpan ActionInterval,
    Action<Machine, MachineContext>? PerformAction,
    Color ActiveColor,
    MaterialAmount BuildCost);

public static class MachineBehaviors
{
    public static readonly MachineBehavior Harvester = new(
        TimeSpan.FromMilliseconds(Tweaks.Machine.HarvesterIntervalMs),
        CrawlAndHarvest,
        Tweaks.Colors.Harvester,
        new MaterialAmount(Tweaks.Machine.HarvesterDirtCost, 0));

    public static readonly MachineBehavior Pylon = new(
        TimeSpan.FromMilliseconds(Tweaks.Machine.PylonIntervalMs),
        ChargeBatteries,
        Tweaks.Colors.Pylon,
        new MaterialAmount(Tweaks.Machine.PylonDirtCost, 0));

    public static readonly MachineBehavior Relay = new(
        Tweaks.World.AdvanceStep,
        PumpHeat,
        Tweaks.Colors.Relay,
        new MaterialAmount(Tweaks.Machine.RelayDirtCost, 0));

    public static MachineBehavior Get(MachineType type) => type switch
    {
        MachineType.Harvester => Harvester,
        MachineType.Pylon => Pylon,
        MachineType.Relay => Relay,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

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

        // SampleAverageHeat is normalised (temperature / 255).
        float temperature = terrain.SampleAverageHeat(machine.Position, Tweaks.Machine.HarvesterHeatSenseRadius) * 255f;
        machine.SenseHeat = temperature / Tweaks.Machine.HarvesterStallTemperature;
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
        if (room <= 0 || machine.Charge < Tweaks.Machine.HarvesterHeatPerHarvest)
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
            if (other.Type != MachineType.Pylon || !other.IsAlive || other.State != MachineState.Planted ||
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
        return !Collision.Raycaster.BresenhamLineAny(pylon.Position, battery.Position,
            pos => Pixel.BlocksLink(terrain.GetPixel(pos)));
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
