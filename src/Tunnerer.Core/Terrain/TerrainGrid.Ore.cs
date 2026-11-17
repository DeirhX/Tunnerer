namespace Tunnerer.Core.Terrain;

using Tunnerer.Core.Thermal;
using Tunnerer.Core.Types;

public partial class TerrainGrid
{
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
