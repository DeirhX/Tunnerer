namespace Emberward.Core.Network;

using Emberward.Core.Collision;
using Emberward.Core.Config;
using Emberward.Core.Entities.Bases;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

public sealed partial class EnergyNetwork
{
    private void RefreshIslands()
    {
        // The map keeps its line-of-sight results. Reuse only live links, in their existing order.
        var nodes = new Dictionary<LinkPoint, Capacitor>();
        var targets = new Dictionary<Capacitor, PowerIsland>();
        foreach (var capacitor in _capacitors)
        {
            var machine = capacitor.Machine;
            if (machine.IsStanding && machine.LinkPoint is { IsEnabled: true } point)
                nodes.Add(point, capacitor);
        }

        // A base's island takes each of its own nodes whose power route ends at that base: the same predicate that
        // covers a harvester (LinkPoint.IsPoweredBy). The route may run through a rival's relay, so this is not a walk
        // over own nodes; a walk would drop such a node while coverage still accepted it.
        foreach (var capacitor in _capacitors)
        {
            var machine = capacitor.Machine;
            if (machine.IsStanding && machine.LinkPoint is { IsEnabled: true } point &&
                machine.OwnerBase is TankBase owner && owner.LinkPoint is { IsEnabled: true } basePoint &&
                machine.OwnerColor == owner.Color && point.IsPoweredBy(basePoint))
                targets.Add(capacitor, IslandOf(owner));
        }

        var frontier = new List<LinkPoint>();
        // A local island's tree: the node each of its nodes was reached from (the generator is the root).
        var reachedFrom = new Dictionary<Capacitor, Capacitor>();

        // The first generator in machine order names an island that has no route to the base.
        foreach (var generator in _capacitors)
        {
            var machine = generator.Machine;
            if (!machine.IsStanding || !machine.Behavior.IsGenerator ||
                machine.LinkPoint is not { IsEnabled: true } point || targets.ContainsKey(generator))
                continue;
            PowerIsland island = LocalIslandOf(machine);
            targets.Add(generator, island);
            ReachFrom(point, island, machine.OwnerBase, machine.OwnerColor);
        }

        // A local island grows over the generator's own nodes that are not already in an island.
        void ReachFrom(LinkPoint root, PowerIsland island, TankBase? owner, int color)
        {
            frontier.Clear();
            frontier.Add(root);
            for (int i = 0; i < frontier.Count; i++)
            {
                var point = frontier[i];
                foreach (var link in point.Links)
                {
                    if (link.Type != LinkType.Live || !link.IsAlive)
                        continue;
                    var other = OtherEnd(link, point);
                    if (!nodes.TryGetValue(other, out var neighbor) ||
                        neighbor.Machine.OwnerBase != owner || neighbor.Machine.OwnerColor != color ||
                        !targets.TryAdd(neighbor, island))
                        continue;
                    reachedFrom.Add(neighbor, nodes[point]);
                    frontier.Add(other);
                }
            }
        }

        foreach (var capacitor in _capacitors)
        {
            if (!capacitor.Machine.Behavior.IsLinkNode)
                continue;
            targets.TryGetValue(capacitor, out var to);
            if (!ReferenceEquals(capacitor.Island, to))
                Move(capacitor, to);
            capacitor.Parent = to == null ? null
                : to.Base != null ? UpstreamNode(capacitor.Machine.LinkPoint!, nodes, targets, to)
                : reachedFrom.GetValueOrDefault(capacitor);
        }
        foreach (var island in _islands)
            island.TreeDirty = true;

        // A moving consumer will pick a new covering node at its next action. A vanished source has no balance
        // to share in the meantime. Reserves belong to the generating machines, so a topology change never loses charge.
        for (int i = _islands.Count - 1; i >= 0; i--)
        {
            var island = _islands[i];
            if (island.Base != null || (island.Producers.Count > 0 &&
                ReferenceEquals(Find(island.Generator!)?.Island, island)))
                continue;
            while (island.Members.Count > 0)
                Move(island.Members[^1], null);
            _islands.RemoveAt(i);
            _localIslandCount--;
        }
    }

    private PowerIsland LocalIslandOf(Machine generator)
    {
        foreach (var island in _islands)
            if (ReferenceEquals(island.Generator, generator))
                return island;
        var local = new PowerIsland { Generator = generator };
        _islands.Add(local);
        _localIslandCount++;
        return local;
    }

    private static LinkPoint OtherEnd(Link link, LinkPoint point) => ReferenceEquals(link.From, point) ? link.To : link.From;

    /// <summary>The nearest live link a carried machine could make to an island run by a local generator.</summary>
    internal LinkPoint? LocalLinkAt(Position at, TankBase owner, MachineList machines, ITerrainPixelReadOnly terrain,
        List<Machine> near) =>
        LocalIslandAt(at, owner, (int)Tweaks.World.MaximumLiveLinkDistance, machines, terrain, near)?.Machine.LinkPoint;

    private Capacitor? LocalIslandAt(Position at, TankBase owner, int radius, MachineList machines, ITerrainPixelReadOnly terrain,
        List<Machine> near)
    {
        if (_localIslandCount == 0)
            return null;
        Capacitor? best = null;
        int bestDistance = radius * radius + 1;
        machines.CollectNear(at, radius, near);
        foreach (var node in near)
        {
            if (!node.StandsFor(owner) || !node.Behavior.IsLinkNode ||
                Find(node) is not { Island.Generator: { IsStanding: true } } capacitor)
                continue;
            int distance = Position.DistanceSquared(at, node.Position);
            if (distance >= bestDistance || !Raycaster.HasOpenLine(terrain, node.Position, at))
                continue;
            best = capacitor;
            bestDistance = distance;
        }
        return best;
    }
}
