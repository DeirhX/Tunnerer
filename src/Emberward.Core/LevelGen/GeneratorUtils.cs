namespace Emberward.Core.LevelGen;

using System.Runtime.CompilerServices;
using Emberward.Core.Types;
using Emberward.Core.Collision;
using Emberward.Core.Terrain;

public static class GeneratorUtils
{
    public static Position GenerateInside(Size size, int border, Random rng)
    {
        return new Position(
            rng.Next(border, size.X - border),
            rng.Next(border, size.Y - border));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsInterior(int x, int y, int w, int h) =>
        x > 0 && x < w - 1 && y > 0 && y < h - 1;

    /// <summary>
    /// CA smoothing rule: given the current pixel and the sum of neighbor byte values
    /// (where LevelGenRock=1, LevelGenDirt=0), returns what the pixel should become.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TerrainPixel SmoothPixel(TerrainPixel current, int rockNeighborSum)
    {
        bool paintRock = (current != TerrainPixel.LevelGenDirt) ? (rockNeighborSum >= 3) : (rockNeighborSum > 4);
        return paintRock ? TerrainPixel.LevelGenRock : TerrainPixel.LevelGenDirt;
    }

    /// <summary>
    /// BFS flood fill through LevelGenDirt pixels using 8-connectivity.
    /// Marks reachable pixels in the visited array starting from the given linear offset.
    /// </summary>
    public static void FloodFillDirt(TerrainGrid terrain, bool[] visited, Queue<int> queue, int startOffset)
    {
        int w = terrain.Width, h = terrain.Height;
        if (!visited[startOffset])
        {
            visited[startOffset] = true;
            queue.Enqueue(startOffset);
        }

        while (queue.Count > 0)
        {
            int idx = queue.Dequeue();
            int cx = idx % w, cy = idx / w;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = cx + dx, ny = cy + dy;
                    if (nx <= 0 || nx >= w - 1 || ny <= 0 || ny >= h - 1) continue;
                    int ni = nx + ny * w;
                    if (visited[ni]) continue;
                    if (terrain.GetPixelRaw(ni) != TerrainPixel.LevelGenDirt) continue;
                    visited[ni] = true;
                    queue.Enqueue(ni);
                }
        }
    }

    public static void DrawLine(TerrainGrid terrain, Position from, Position to, TerrainPixel value)
    {
        Raycaster.BresenhamLine(from, to, pos => terrain.SetPixelRaw(pos, value));
    }

    public static void DrawThickLine(TerrainGrid terrain, Position from, Position to, TerrainPixel value, int radius)
    {
        int w = terrain.Width, h = terrain.Height;
        Raycaster.BresenhamLine(from, to, center =>
        {
            int cx = center.X, cy = center.Y;
            for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (IsInterior(nx, ny, w, h))
                        terrain.SetPixelRaw(new Position(nx, ny), value);
                }
        });
    }

    /// <summary>Whether <paramref name="pos"/> lies closer than the square root of <paramref name="minDistanceSq"/> to any of <paramref name="others"/>.</summary>
    public static bool TooClose(Position pos, IReadOnlyList<Position> others, int minDistanceSq)
    {
        foreach (var other in others)
            if (Position.DistanceSquared(pos, other) < minDistanceSq)
                return true;
        return false;
    }

    public static void SetOutside(TerrainGrid terrain, TerrainPixel value)
    {
        int w = terrain.Width, h = terrain.Height;
        for (int x = 0; x < w; x++)
        {
            terrain.SetPixelRaw(new Position(x, 0), value);
            terrain.SetPixelRaw(new Position(x, h - 1), value);
        }
        for (int y = 1; y < h - 1; y++)
        {
            terrain.SetPixelRaw(new Position(0, y), value);
            terrain.SetPixelRaw(new Position(w - 1, y), value);
        }
    }
}
