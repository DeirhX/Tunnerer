namespace Emberward.Core.Entities.Emberites;

using Emberward.Core.Config;
using Emberward.Core.Snapshots;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

/// <summary>
/// A coarse cost-to-goal field over a window of blocks around a goal (a hunted tank, or a nest to go home to): where
/// each block costs what crossing it takes at the creatures' speeds (open ground fast, soil slow, rock and bases
/// never), so a creature that steps to the cheapest neighbour prefers a tunnel to a shorter line through dirt.
/// Rebuilt every <see cref="Tweaks.Emberites.FieldRefreshTicks"/> while something reads it. It is built from terrain as it
/// was, so it is part of a snapshot.
/// </summary>
public sealed class EmberiteFlowField
{
    private const int Unreachable = int.MaxValue;
    private const int Window = Tweaks.Emberites.FieldWindowBlocks;
    private const int Block = Tweaks.Emberites.FieldBlockCells;

    /// <summary>What the field leads to: 0 a tank (<see cref="Id"/> its index), 1 a nest (its index).</summary>
    public int Kind { get; init; }

    public int Id { get; init; }

    /// <summary>The block at the window's top left (may lie off the map).</summary>
    public int OriginX { get; private set; }

    public int OriginY { get; private set; }

    /// <summary>The goal's block, which the field was built around.</summary>
    public Position GoalBlock { get; private set; }

    /// <summary>Ticks since it was built.</summary>
    public int Age { get; private set; }

    /// <summary>Ticks since a creature last read it.</summary>
    public int Idle { get; set; }

    /// <summary>A creature read it this step.</summary>
    public bool Used { get; set; }

    public bool IsBuilt { get; private set; }

    /// <summary>Cost to the goal of each block of the window, row by row; <see cref="int.MaxValue"/> where it cannot be reached.</summary>
    public int[] Cost { get; private set; } = [];

    public void Tick() => Age++;

    /// <summary>Whether the field is out of date for a goal now at <paramref name="goal"/>.</summary>
    public bool NeedsBuild(Position goal) =>
        !IsBuilt || Age >= Tweaks.Emberites.FieldRefreshTicks || GoalBlock != new Position(goal.X / Block, goal.Y / Block);

    /// <summary>
    /// Builds the field around <paramref name="goal"/> by Dijkstra over the window's blocks. A nest's ground
    /// (<paramref name="zoneCentre"/>, <paramref name="zoneRadius"/>) counts as soil, which its creatures burrow freely.
    /// </summary>
    internal void Build(TerrainGrid terrain, Position goal, PriorityQueue<int, long> queue, EmberiteTrails trails, int tick, Position zoneCentre = default, int zoneRadius = 0)
    {
        GoalBlock = new Position(goal.X / Block, goal.Y / Block);
        OriginX = GoalBlock.X - Window / 2;
        OriginY = GoalBlock.Y - Window / 2;
        if (Cost.Length != Window * Window)
            Cost = new int[Window * Window];
        Array.Fill(Cost, Unreachable);
        Age = 0;
        IsBuilt = true;

        queue.Clear();
        int start = Window / 2 + Window / 2 * Window;
        Cost[start] = 0;
        queue.Enqueue(start, 0);
        while (queue.TryDequeue(out int index, out long priority))
        {
            int cost = (int)(priority >> 20);
            if (cost > Cost[index])
                continue;
            int bx = index % Window, by = index / Window;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = bx + dx, ny = by + dy;
                    if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= Window || ny >= Window)
                        continue;
                    int step = BlockCost(terrain, OriginX + nx, OriginY + ny, zoneCentre, zoneRadius, trails.IsLane(OriginX + nx, OriginY + ny, tick));
                    if (step < 0)
                        continue;
                    if (dx != 0 && dy != 0)
                        step = step * 14 / 10;
                    int next = cost + step;
                    int ni = nx + ny * Window;
                    if (next < Cost[ni])
                    {
                        Cost[ni] = next;
                        queue.Enqueue(ni, ((long)next << 20) | (uint)ni);
                    }
                }
        }
    }

    /// <summary>
    /// The way to go from <paramref name="x"/>, <paramref name="y"/>: toward the centre of the cheapest neighbouring
    /// block. False when the position is outside the window or cannot reach the goal.
    /// </summary>
    public bool TryDirection(float x, float y, out float dirX, out float dirY)
    {
        dirX = dirY = 0f;
        if (!IsBuilt)
            return false;
        int bx = (int)MathF.Floor(x / Block) - OriginX, by = (int)MathF.Floor(y / Block) - OriginY;
        if (bx < 0 || by < 0 || bx >= Window || by >= Window || Cost[bx + by * Window] == Unreachable)
            return false;
        int best = Cost[bx + by * Window];
        int bestX = bx, bestY = by;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = bx + dx, ny = by + dy;
                if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= Window || ny >= Window)
                    continue;
                int cost = Cost[nx + ny * Window];
                if (cost < best)
                {
                    best = cost;
                    bestX = nx;
                    bestY = ny;
                }
            }
        // At the goal's own block (or a local minimum): the caller steers straight to the goal.
        if (bestX == bx && bestY == by)
            return false;
        dirX = (OriginX + bestX) * Block + Block * 0.5f - x;
        dirY = (OriginY + bestY) * Block + Block * 0.5f - y;
        float length = MathF.Sqrt(dirX * dirX + dirY * dirY);
        if (length < 1e-4f)
            return false;
        dirX /= length;
        dirY /= length;
        return true;
    }

    /// <summary>
    /// What crossing block (<paramref name="bx"/>, <paramref name="by"/>) costs, or -1 when it cannot be crossed (more
    /// than a quarter of its cells are rock, ore or base, or it is off the map). A <paramref name="lane"/>'s soil costs as open ground.
    /// </summary>
    private static int BlockCost(TerrainGrid terrain, int bx, int by, Position zoneCentre, int zoneRadius, bool lane)
    {
        int open = 0, soil = 0, blocked = 0;
        for (int y = by * Block; y < by * Block + Block; y++)
            for (int x = bx * Block; x < bx * Block + Block; x++)
            {
                var cell = new Position(x, y);
                if (!terrain.IsInside(cell))
                    return -1;
                switch (EmberiteSwarm.Classify(terrain.GetPixelRaw(cell), cell, zoneCentre, zoneRadius))
                {
                    case EmberiteGround.Open: open++; break;
                    case EmberiteGround.Soil: soil++; break;
                    default: blocked++; break;
                }
            }
        if (blocked > Block * Block / 4)
            return -1;
        if (lane)
        {
            // A lane is worn soil: creatures run through it as through open ground.
            open += soil;
            soil = 0;
        }
        int passable = open + soil;
        return (open * Tweaks.Emberites.OpenBlockCost + soil * Tweaks.Emberites.SoilBlockCost) / passable;
    }
}

/// <summary>What a creature finds in a cell.</summary>
public enum EmberiteGround : byte
{
    /// <summary>Open ground: a tunnel, scorch. They run through it.</summary>
    Open,

    /// <summary>Soil, or their own nest's ground: they burrow through it, slowly.</summary>
    Soil,

    /// <summary>Rock, concrete, ore, a base, the map's edge: it stops them.</summary>
    Blocked,
}
