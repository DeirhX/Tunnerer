namespace Tunnerer.Core.Terrain;

/// <summary>
/// Pixel access plus thermal access, for entities (machines, tanks) that both read the terrain and exchange heat with it.
/// </summary>
public interface ITerrainAccess : ITerrainPixelAccessor, ITerrainHeatSource
{
}
