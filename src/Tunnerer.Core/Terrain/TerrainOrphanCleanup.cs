namespace Tunnerer.Core.Terrain;

using Tunnerer.Core.Types;

/// <summary>
/// Removes small solid clumps that the visual smoothing pipeline (wide Gaussian SDF)
/// makes invisible but that still participate in collision. Run after terrain modification
/// (dig, shrapnel) to keep physics aligned with what the player sees.
/// </summary>
public static class TerrainOrphanCleanup
{
    private const int MaxSolidNeighbors = 2;

    public static void CleanupArea(ITerrainPixelReader terrain, Position center, int radius, int passes = 2)
    {
        int w = terrain.Width, h = terrain.Height;
        int minX = Math.Max(1, center.X - radius);
        int minY = Math.Max(1, center.Y - radius);
        int maxX = Math.Min(w - 2, center.X + radius);
        int maxY = Math.Min(h - 2, center.Y + radius);

        if (minX > maxX || minY > maxY)
            return;

        for (int pass = 0; pass < passes; pass++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    var pos = new Position(x, y);
                    var pixel = terrain.GetPixelRaw(pos);
                    if (!Pixel.IsDiggable(pixel))
                        continue;

                    int solidCount = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            if (IsSolidForCleanup(terrain.GetPixelRaw(new Position(x + dx, y + dy))))
                                solidCount++;
                        }
                    }

                    if (solidCount <= MaxSolidNeighbors)
                        terrain.SetPixel(pos, TerrainPixel.Blank);
                }
            }
        }
    }

    private static bool IsSolidForCleanup(TerrainPixel p) =>
        p is not TerrainPixel.Blank and not TerrainPixel.DirtGrow;
}
