namespace Tunnerer.Core.Entities.Links;

using Tunnerer.Core.Collision;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

/// <summary>A node that cools tanks around it: the base, or a powered relay or pylon of that base.</summary>
/// <param name="Strength">Cooling strength, 1 at the base, falling by <c>Coverage.HopFalloff</c> per link hop.</param>
/// <param name="Point">The node's point in the link network (null only for a base without one).</param>
public readonly record struct CoverageNode(Position Position, int Radius, float Strength, LinkPoint? Point = null);

/// <summary>Whether a position is inside a base's network coverage, and through which node.</summary>
/// <param name="Point">The covering node's point in the link network, the way ore unloaded there travels home.</param>
public readonly record struct TankCoverage(bool Covered, float Strength, Position Node, LinkPoint? Point = null)
{
    public static readonly TankCoverage None = new(false, 0f, default);
}

/// <summary>
/// The ground a base's network keeps cool for its tanks (DESIGN.md §4.6). Each node covers a circle; a position is
/// covered by a node when it is within that circle and in direct line of sight through open ground (the same rule
/// links use), and the strongest such node wins.
/// </summary>
public static class NetworkCoverage
{
    public static void CollectNodes(TankBase owner, IReadOnlyList<Machine> machines, List<CoverageNode> into)
    {
        into.Clear();
        int bonus = owner.Upgrades.CoverageBonusCells;
        into.Add(new CoverageNode(owner.Position, Tweaks.Coverage.BaseRadius + bonus, 1f, owner.LinkPoint));
        foreach (var machine in machines)
        {
            if (!machine.IsAlive || machine.State != MachineState.Planted || machine.OwnerBase != owner ||
                !machine.Behavior.IsLinkNode ||
                machine.LinkPoint is not { IsPowered: true, Hops: >= 0 } point)
                continue;
            into.Add(new CoverageNode(machine.Position, Tweaks.Coverage.NodeRadius + bonus, StrengthForHops(point.Hops), point));
        }
    }

    public static float StrengthForHops(int hops) => DeterministicMath.Pow(Tweaks.Coverage.HopFalloff, Math.Max(0, hops));

    public static TankCoverage At(Position position, IReadOnlyList<CoverageNode> nodes, ITerrainPixelReadOnly terrain)
    {
        var best = TankCoverage.None;
        foreach (var node in nodes)
        {
            if (node.Strength <= best.Strength)
                continue;
            if (Position.DistanceSquared(position, node.Position) > node.Radius * node.Radius)
                continue;
            if (!Raycaster.HasOpenLine(terrain, node.Position, position))
                continue;
            best = new TankCoverage(true, node.Strength, node.Position, node.Point);
        }
        return best;
    }
}
