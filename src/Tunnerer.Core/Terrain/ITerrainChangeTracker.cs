namespace Tunnerer.Core.Terrain;

using Tunnerer.Core.Types;

/// <summary>
/// Tracks terrain cells that have been modified since the last clear.
/// Used by the Desktop render pipeline to incrementally update the display.
/// </summary>
public interface ITerrainChangeTracker
{
    IReadOnlyList<Position> GetChangeList();
    void ClearChangeList();
}
