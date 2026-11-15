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
public readonly record struct MachineContext(ITerrainAccess Terrain, Func<Machine, Position, bool> IsOccupied)
{
    public static MachineContext ForTerrain(ITerrainAccess terrain) => new(terrain, static (_, _) => false);
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

    public static readonly MachineBehavior Charger = new(
        TimeSpan.FromMilliseconds(Tweaks.Machine.ChargerIntervalMs),
        null,
        Tweaks.Colors.Charger,
        new MaterialAmount(Tweaks.Machine.ChargerDirtCost, 0));

    public static readonly MachineBehavior Relay = new(
        Tweaks.World.AdvanceStep,
        PumpHeat,
        Tweaks.Colors.Relay,
        new MaterialAmount(Tweaks.Machine.RelayDirtCost, 0));

    public static MachineBehavior Get(MachineType type) => type switch
    {
        MachineType.Harvester => Harvester,
        MachineType.Charger => Charger,
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

        machine.LastPumpLimitedByBase = machine.OwnerBase.ReactorRoomEnergy < Tweaks.Machine.RelayHeatPumpPerTick;
        machine.LastPumpedEnergy = machine.OwnerBase.AbsorbHeatFrom(
            terrain, machine.Position, Tweaks.Machine.RelayCoolingRadius, Tweaks.Machine.RelayHeatPumpPerTick);
        Links.LinkMap.CarryHeat(machine.LinkPoint, machine.LastPumpedEnergy);
    }

    /// <summary>
    /// A roaming harvester: each action it eats the settled dirt within <c>HarvesterMineRadius</c> of itself into its
    /// owner base's store; when nothing is left in reach it crawls one cell toward the nearest dirt within
    /// <c>HarvestRange</c> of where it was planted, carving its own tunnel. Working heats the ground, and it stalls
    /// while that ground is too hot, so it needs a relay nearby to keep going. It idles while the store is full.
    /// </summary>
    private static void CrawlAndHarvest(Machine machine, MachineContext ctx)
    {
        var terrain = ctx.Terrain;

        // Too hot to work: wait for the ground to cool (a linked relay nearby speeds that up a lot).
        // SampleAverageHeat is normalised (temperature / 255).
        float temperature = terrain.SampleAverageHeat(machine.Position, Tweaks.Machine.HarvesterHeatSenseRadius) * 255f;
        machine.SenseHeat = temperature / Tweaks.Machine.HarvesterStallTemperature;
        machine.IsStalled = temperature >= Tweaks.Machine.HarvesterStallTemperature;
        if (machine.IsStalled)
            return;

        // The dirt goes to the owner base's store; with the store full there is nowhere to put it.
        var store = machine.OwnerBase?.Materials;
        if (store == null)
            return;
        int room = store.DirtCapacity - store.Dirt;
        if (room <= 0)
            return;

        int mined = MineAround(machine, terrain, room);
        if (mined == 0 && FindNearestDirt(machine, terrain) is Position target)
        {
            Offset step = ChooseStep(machine, ctx, target);
            if (step != default)
            {
                machine.Position += step;
                machine.Heading = step;
                mined = MineAround(machine, terrain, room);
            }
        }

        if (mined == 0)
            return;
        store.Add(new MaterialAmount(mined, 0));
        // Working gives off heat into the ground beside the harvester, once per action that mined something.
        // The work itself creates this energy (no reactor is debited), so the ledger must book it as injected.
        double heat = terrain.AddEnergyInRadiusArea(machine.Position, Tweaks.Machine.HarvesterHeatRadius,
            Tweaks.Machine.HarvesterHeatPerHarvest, TerrainGrid.ThermalField.Terrain);
        terrain.Ledger.RecordInjected(heat);
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
    /// ties and never straight back, through ground its footprint can cross (open, dirt or scorched; not rock or
    /// base) and not onto a tank or another machine. Zero when it is boxed in.
    /// </summary>
    private static Offset ChooseStep(Machine machine, MachineContext ctx, Position target)
    {
        Offset best = default;
        int bestScore = int.MaxValue;
        var back = new Offset(-machine.Heading.X, -machine.Heading.Y);
        // Planted right beside a relay or tank it already overlaps: let it crawl out rather than freeze.
        bool alreadyCrowded = ctx.IsOccupied(machine, machine.Position);
        foreach (var step in Steps)
        {
            if (machine.Heading != default && step == back) continue;
            var next = machine.Position + step;
            if (!InWorkArea(machine, next) || !CanStand(machine, ctx.Terrain, next) ||
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
                var pixel = terrain.GetPixelRaw(pos);
                if (pixel != TerrainPixel.Blank && !Pixel.IsDirt(pixel) && !Pixel.IsScorched(pixel)) return false;
            }
        return true;
    }
}
