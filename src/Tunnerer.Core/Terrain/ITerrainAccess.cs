namespace Tunnerer.Core.Terrain;

/// <summary>
/// Pixel access plus thermal access, for entities (machines, tanks) that both read the terrain and exchange heat with it.
/// </summary>
public interface ITerrainAccess : ITerrainPixelAccessor, ITerrainHeatSource
{
    /// <summary>Whether the circle holds plain ore and lump core (<see cref="TerrainGrid.FindOre"/>).</summary>
    void FindOre(Types.Position center, int radius, out bool plain, out bool core);
}
