namespace Tunnerer.Core.Terrain;

using Tunnerer.Core.Thermal;
using Tunnerer.Core.Types;

public partial class TerrainGrid
{
    // Side of the tiles ore cells are counted in, as a power of two.
    private const int OreTileShift = 5;

    // Ore cells (plain and core) per tile, kept by every pixel write, so that a place with no ore is known without
    // looking at its cells.
    private int[] _oreCellsOfTile = [];
    private int _oreTilesX;

    /// <summary>
    /// Whether the circle of <paramref name="radius"/> around <paramref name="center"/> holds plain ore
    /// (<paramref name="plain"/>) and lump core (<paramref name="core"/>). Where the tiles under it hold no ore at
    /// all, which is most of the map, no cell is read.
    /// </summary>
    public void FindOre(Position center, int radius, out bool plain, out bool core)
    {
        plain = core = false;
        int minX = Math.Max(0, center.X - radius), maxX = Math.Min(Width - 1, center.X + radius);
        int minY = Math.Max(0, center.Y - radius), maxY = Math.Min(Height - 1, center.Y + radius);
        if (maxX < minX || maxY < minY)
            return;

        bool any = false;
        for (int ty = minY >> OreTileShift; ty <= maxY >> OreTileShift && !any; ty++)
            for (int tx = minX >> OreTileShift; tx <= maxX >> OreTileShift && !any; tx++)
                any = _oreCellsOfTile[tx + ty * _oreTilesX] > 0;
        if (!any)
            return;

        for (int y = minY; y <= maxY; y++)
        {
            int dy = y - center.Y;
            for (int x = minX; x <= maxX; x++)
            {
                int dx = x - center.X;
                if (dx * dx + dy * dy > radius * radius)
                    continue;
                TerrainPixel pixel = _data[x + y * Width];
                if (Pixel.IsOreCore(pixel))
                    core = true;
                else if (Pixel.IsOre(pixel))
                    plain = true;
            }
        }
    }

    private void NoteOreWrite(int offset, TerrainPixel was, TerrainPixel now)
    {
        bool wasOre = Pixel.IsOre(was), isOre = Pixel.IsOre(now);
        if (wasOre != isOre)
            _oreCellsOfTile[OreTileOf(offset)] += isOre ? 1 : -1;
    }

    /// <summary>Counts the ore from the grid, after pixels were written in bulk.</summary>
    private void RecountOre()
    {
        if (_oreCellsOfTile.Length == 0)
        {
            _oreTilesX = (Width >> OreTileShift) + 1;
            _oreCellsOfTile = new int[_oreTilesX * ((Height >> OreTileShift) + 1)];
        }
        Array.Clear(_oreCellsOfTile);
        for (int offset = 0; offset < _data.Length; offset++)
            if (Pixel.IsOre(_data[offset]))
                _oreCellsOfTile[OreTileOf(offset)]++;
    }

    private int OreTileOf(int offset) => (offset % Width >> OreTileShift) + (offset / Width >> OreTileShift) * _oreTilesX;

    /// <summary>
    /// Level generation: turns one cell of solid ground into ore (<see cref="LevelGen.OreDeposits"/>). Specks only
    /// replace dirt (<paramref name="dirtOnly"/>); pockets and lumps also replace plain rock. A core cell is pinned at
    /// its fixed temperature at once, like a hot rock pocket. Returns false (and changes nothing) for any other cell
    /// or one on the border margin.
    /// </summary>
    public bool PlaceOre(Position pos, bool core, bool dirtOnly)
    {
        if (pos.X < 1 || pos.Y < 1 || pos.X >= Width - 1 || pos.Y >= Height - 1)
            return false;
        TerrainPixel existing = _data[pos.X + pos.Y * Width];
        bool fits = dirtOnly ? Pixel.IsDirt(existing) || existing == TerrainPixel.DirtGrow : IsSolidGround(existing);
        if (!fits)
            return false;

        if (core)
            SetPinnedPixel(pos, TerrainPixel.EmberiteCore, ThermalMaterial.EmberiteCore);
        else
            SetPixel(pos, TerrainPixel.Emberite);
        return true;
    }
}
