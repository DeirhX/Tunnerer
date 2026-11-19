namespace Emberward.Core.Terrain;

using Emberward.Core.Config;

public partial class TerrainGrid
{
    public void DecorateTerrain(int seed)
    {
        var rng = new Random(seed);
        int w = Width, h = Height;

        PlaceConcreteRuins(rng, w, h);
        CleanupIsolatedPixels(w, h);
        NoteBulkWrite();
    }

    /// <summary>
    /// Removes single-pixel anomalies that cause rendering artifacts.
    /// An isolated pixel is one whose solid/empty state differs from 6+ of its 8 neighbors.
    /// </summary>
    private void CleanupIsolatedPixels(int w, int h)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            for (int y = 1; y < h - 1; y++)
            {
                int row = y * w;
                for (int x = 1; x < w - 1; x++)
                {
                    int offset = row + x;
                    var p = _data[offset];
                    bool isSolid = IsSolidForCleanup(p);

                    int solidNeighbors = 0;
                    TerrainPixel dominantSolid = TerrainPixel.Rock;
                    TerrainPixel dominantEmpty = TerrainPixel.Blank;

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int nRow = (y + dy) * w;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            var np = _data[nRow + x + dx];
                            if (IsSolidForCleanup(np))
                            {
                                solidNeighbors++;
                                dominantSolid = np;
                            }
                            else
                            {
                                dominantEmpty = np;
                            }
                        }
                    }

                    if (isSolid && solidNeighbors <= 1)
                        _data[offset] = dominantEmpty;
                    else if (!isSolid && solidNeighbors >= 7)
                        _data[offset] = dominantSolid;
                }
            }
        }
    }

    private static bool IsSolidForCleanup(TerrainPixel p) =>
        p is not (TerrainPixel.Blank or TerrainPixel.DirtGrow);

    private static bool IsCaveFloor(TerrainPixel p) =>
        p is TerrainPixel.Blank or TerrainPixel.Dirt or TerrainPixel.DirtGrow;

    private void PlaceConcreteRuins(Random rng, int w, int h)
    {
        int ruinCount = (w * h) / Tweaks.LevelGen.RuinAreaPerRuin;

        for (int r = 0; r < ruinCount; r++)
        {
            int cx = rng.Next(6, w - 6);
            int cy = rng.Next(6, h - 6);

            if (!IsCaveFloor(_data[cx + cy * w])) continue;

            int len = rng.Next(Tweaks.LevelGen.RuinWallMinLength, Tweaks.LevelGen.RuinWallMaxLength);
            bool horizontal = rng.Next(2) == 0;
            for (int i = 0; i < len; i++)
            {
                int nx = horizontal ? cx + i : cx;
                int ny = horizontal ? cy : cy + i;
                if (nx < 1 || nx >= w - 1 || ny < 1 || ny >= h - 1) continue;
                int offset = nx + ny * w;
                if (IsCaveFloor(_data[offset]))
                    _data[offset] = TerrainPixel.Concrete;
            }
        }
    }
}
