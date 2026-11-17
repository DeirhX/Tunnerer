namespace Tunnerer.Core.Terrain;

using Tunnerer.Core.Types;

/// <summary>
/// Lists the terrain cells whose pixel was set. Whatever follows the terrain instead of rereading it works from
/// this list: the Desktop render pipeline, and fog of war (<c>Sensing/VisionMap</c>).
/// </summary>
/// <remarks>
/// The world alone trims the list, and readers only read it. At the end of a step the world marks what is listed as
/// seen, and at the start of the next it drops that, so after a step the list holds the cells that step changed (and
/// any changed since the step before it ended).
/// </remarks>
public interface ITerrainChangeTracker
{
    IReadOnlyList<Position> GetChangeList();

    /// <summary>Everything listed so far has been looked at; <see cref="DropSeenChanges"/> may forget it.</summary>
    void MarkChangesSeen();

    /// <summary>Forgets the changes that were listed when <see cref="MarkChangesSeen"/> was last called.</summary>
    void DropSeenChanges();
}
