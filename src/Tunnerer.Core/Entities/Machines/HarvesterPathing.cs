namespace Tunnerer.Core.Entities.Machines;

using Tunnerer.Core.Config;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

/// <summary>
/// Where a roaming harvester may stand and how it gets to ore: the ground its footprint can cross, the flood over its
/// work area that finds the ore nearest by path (<see cref="FindReachableOre"/>), and the single greedy step it takes
/// toward a pylon (<see cref="ChooseStep"/>).
/// </summary>
internal static class HarvesterPathing
{
    // The eight crawl directions, in a fixed order so ties resolve the same way every run.
    private static readonly Offset[] Steps =
    [
        new(1, 0), new(1, 1), new(0, 1), new(-1, 1), new(-1, 0), new(-1, -1), new(0, -1), new(1, -1),
    ];

    // The same steps as the flood takes them, straight ones first, so that among equally short paths it finds one that
    // keeps to a row or column where it can (a harvester stays on the network only in sight of a node).
    private static readonly Offset[] FloodSteps =
    [
        new(1, 0), new(0, 1), new(-1, 0), new(0, -1), new(1, 1), new(-1, 1), new(-1, -1), new(1, -1),
    ];

    // Scratch for one flood, reused between calls. Per thread, because worlds on different threads (tests, replays)
    // may flood at the same time; nothing in it outlives a call, so it never carries state between ticks.
    [ThreadStatic]
    private static FloodScratch? t_scratch;

    private sealed class FloodScratch
    {
        public int[] Visited = [];
        public int[] Queue = [];
        public byte[] FirstStep = [];
        public int Generation;

        public void Begin(int cells)
        {
            if (Visited.Length < cells)
            {
                Visited = new int[cells];
                Queue = new int[cells];
                FirstStep = new byte[cells];
                Generation = 0;
            }
            Generation++;
        }
    }

    public static bool InWorkArea(Machine machine, Position p)
    {
        int range = Tweaks.Machine.HarvestRange;
        return Position.DistanceSquared(p, machine.Anchor) <= range * range;
    }

    /// <summary>
    /// The plain ore the harvester can work soonest: the cell in its work area within <c>Ore.HarvesterReach</c> of the
    /// centre nearest by path that its footprint can stand on (its own position first), or null when no plain ore in
    /// the area can be got within reach of. <paramref name="firstStep"/> is the first step of that path, zero when the
    /// ore is in reach of where it stands. <paramref name="coreLeft"/> says whether the area still holds core ore,
    /// which harvesters leave to the tank.
    /// </summary>
    /// <remarks>
    /// The flood crosses what <see cref="ChooseStep"/> may step on: ground the footprint can stand on, not on a tank
    /// or another machine, inside the work area (from outside it, a harvester back from a pylon may only step closer
    /// to its anchor). It stops at the first centre with ore in reach, so its cost follows the distance to the ore;
    /// only ore that is in the area but out of reach makes it cover the whole area.
    /// </remarks>
    public static Position? FindReachableOre(Machine machine, MachineContext ctx, out Offset firstStep, out bool coreLeft)
    {
        firstStep = default;
        var terrain = ctx.Terrain;
        if (!HasPlainOre(machine, terrain, out coreLeft))
            return null;
        if (OreInReach(machine, terrain, machine.Position) is Position here)
            return here;

        var anchor = machine.Anchor;
        int range = Tweaks.Machine.HarvestRange;
        int rangeSquared = range * range;
        // Every centre the flood may visit is no farther from the anchor than the work area's edge or the start.
        int radius = range;
        int startDist = Position.DistanceSquared(machine.Position, anchor);
        while (radius * radius < startDist)
            radius++;
        int side = 2 * radius + 1;
        var scratch = t_scratch ??= new FloodScratch();
        scratch.Begin(side * side);
        int generation = scratch.Generation;
        int IndexOf(Position p) => (p.X - anchor.X + radius) + (p.Y - anchor.Y + radius) * side;

        // Planted right beside a relay or tank it already overlaps: let it crawl out rather than freeze.
        bool alreadyCrowded = ctx.IsOccupied(machine, machine.Position);
        int head = 0, tail = 0;
        int startIndex = IndexOf(machine.Position);
        scratch.Visited[startIndex] = generation;
        scratch.Queue[tail++] = startIndex;
        while (head < tail)
        {
            int index = scratch.Queue[head++];
            var at = new Position(index % side - radius + anchor.X, index / side - radius + anchor.Y);
            int anchorDist = Position.DistanceSquared(at, anchor);
            for (int i = 0; i < FloodSteps.Length; i++)
            {
                var next = at + FloodSteps[i];
                int nextDist = Position.DistanceSquared(next, anchor);
                if (nextDist > rangeSquared && nextDist >= anchorDist)
                    continue;
                int nextIndex = IndexOf(next);
                if (scratch.Visited[nextIndex] == generation)
                    continue;
                scratch.Visited[nextIndex] = generation;
                if (!CanStand(machine, terrain, next) || (!alreadyCrowded && ctx.IsOccupied(machine, next)))
                    continue;
                byte first = index == startIndex ? (byte)i : scratch.FirstStep[index];
                if (OreInReach(machine, terrain, next) is Position ore)
                {
                    firstStep = FloodSteps[first];
                    return ore;
                }
                scratch.FirstStep[nextIndex] = first;
                scratch.Queue[tail++] = nextIndex;
            }
        }
        return null;
    }

    /// <summary>Whether the work area holds any plain ore, and (<paramref name="coreLeft"/>) any core ore.</summary>
    private static bool HasPlainOre(Machine machine, ITerrainAccess terrain, out bool coreLeft)
    {
        coreLeft = false;
        bool plain = false;
        int range = Tweaks.Machine.HarvestRange;
        for (int dy = -range; dy <= range; dy++)
            for (int dx = -range; dx <= range; dx++)
            {
                if (dx * dx + dy * dy > range * range) continue;
                var pos = new Position(machine.Anchor.X + dx, machine.Anchor.Y + dy);
                if (!terrain.IsInside(pos)) continue;
                var pixel = terrain.GetPixelRaw(pos);
                if (Pixel.IsOreCore(pixel))
                    coreLeft = true;
                else if (Pixel.IsOre(pixel))
                    plain = true;
            }
        return plain;
    }

    /// <summary>
    /// The plain ore cell of the work area closest to <paramref name="center"/> and within the harvester's reach of
    /// it (first in scan order on ties), or null.
    /// </summary>
    private static Position? OreInReach(Machine machine, ITerrainAccess terrain, Position center)
    {
        int reach = Tweaks.Ore.HarvesterReach;
        Position? best = null;
        int bestDist = int.MaxValue;
        for (int dy = -reach; dy <= reach; dy++)
            for (int dx = -reach; dx <= reach; dx++)
            {
                int d = dx * dx + dy * dy;
                if (d > reach * reach || d >= bestDist) continue;
                var pos = new Position(center.X + dx, center.Y + dy);
                if (!terrain.IsInside(pos) || !InWorkArea(machine, pos)) continue;
                var pixel = terrain.GetPixelRaw(pos);
                if (!Pixel.IsOre(pixel) || Pixel.IsOreCore(pixel)) continue;
                bestDist = d;
                best = pos;
            }
        return best;
    }

    /// <summary>
    /// The step that brings the harvester closest to <paramref name="target"/>, preferring its current heading on
    /// ties and never straight back, through ground its footprint can cross (<see cref="IsCrawlable"/>: not rock,
    /// concrete or base) and not onto a tank or another machine. Zero when it is boxed in.
    /// </summary>
    public static Offset ChooseStep(Machine machine, MachineContext ctx, Position target)
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
            if (!CanStand(machine, ctx.Terrain, next) || (!alreadyCrowded && ctx.IsOccupied(machine, next)))
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
