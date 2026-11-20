namespace Emberward.Core.Network;

using System.Diagnostics;
using Emberward.Core.Config;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

/// <summary>How hot a node of the energy network runs from the charge it carries (<see cref="Tweaks.Throughput"/>).</summary>
public enum ThroughputHeat
{
    Normal,

    /// <summary>Its ground is hot: it passes on a share of its limit.</summary>
    Throttled,

    /// <summary>Its ground is hotter still: it passes on less, and loses health at each check.</summary>
    Overheated,
}

/// <summary>
/// Throughput (DESIGN.md §4.9): a link node passes on at most its limit a tick to the machines that take their charge
/// through it, so a chain is as fast as its slowest node. Each island is a tree. A node's parent is the next node of the
/// island on its power route (<see cref="LinkPoint.UpstreamLink"/>, the route <see cref="LinkPoint.IsPoweredBy"/>
/// follows), or the node a local island reached it from; a harvester hangs off the node that covers it; the base (or a
/// local island's generator, which is itself a node) is the root.
/// <para>
/// Cost follows change. The tree is worked out again only when the island's demand or shape changes (a capacitor starts
/// or stops filling, a member moves, links change, a node's limit changes), and then only over the capacitors that are
/// filling and the nodes on their way: demand is added up from the leaves, each node caps what it passes on (feeding
/// the higher tiers first), and each filling capacitor is left the share its route allows (its
/// <see cref="Capacitor.Allowance"/>). A network at rest has nothing filling and works out nothing.
/// </para>
/// <para>
/// Energy: each node on the way loses <c>Throughput.LossFraction</c> of what it passes on. The island draws that with
/// the top-up, and the loss joins the node's pending charge, which settling turns into heat at the node through the heat
/// writers (waking its tile) and books as charge turned back into heat. A node that carried charge is checked at its
/// settling: on hot ground it is throttled, and hotter still it is slowed further and damaged.
/// </para>
/// </summary>
public sealed partial class EnergyNetwork
{
    private static readonly Comparison<Capacitor> DeepestFirst = (a, b) =>
        a.Depth != b.Depth ? b.Depth.CompareTo(a.Depth) : a.Machine.Sequence.CompareTo(b.Machine.Sequence);

    // Scratch for RebuildTree, empty between calls: the nodes of the tree being worked out, and one route.
    private readonly List<Capacitor> _treeNodes = new();
    private readonly List<Capacitor> _treeRoute = new();
    // Marks the nodes already in the tree being worked out (Capacitor.TreeStamp).
    private int _treeStamp;

    /// <summary>
    /// Sets the multiple of <c>Throughput.NodeLimitPerTick</c> <paramref name="machine"/> passes on (the Conduit upgrade,
    /// <see cref="MachineUpgrades.ThroughputScaleOf"/>). Its island works its tree out again before it next tops up.
    /// </summary>
    public void SetThroughputScale(Machine machine, double scale)
    {
        if (Find(machine) is not Capacitor capacitor || capacitor.ThroughputScale == scale)
            return;
        capacitor.ThroughputScale = scale;
        MarkTreeDirty(capacitor.Island);
    }

    /// <summary>The most <paramref name="node"/> passes on in one tick: its Conduit level, capped, then slowed by heat.</summary>
    private static double LimitOf(Capacitor node)
    {
        double heat = node.Heat switch
        {
            ThroughputHeat.Throttled => Tweaks.Throughput.ThrottledScale,
            ThroughputHeat.Overheated => Tweaks.Throughput.OverheatedScale,
            _ => 1.0,
        };
        return Math.Min(Tweaks.Throughput.NodeLimitPerTick * node.ThroughputScale, Tweaks.Throughput.CeilingPerTick) * heat;
    }

    private static void MarkTreeDirty(PowerIsland? island)
    {
        if (island != null)
            island.TreeDirty = true;
    }

    private static void SetParent(Capacitor capacitor, Capacitor? parent)
    {
        if (ReferenceEquals(capacitor.Parent, parent))
            return;
        capacitor.Parent = parent;
        MarkTreeDirty(capacitor.Island);
    }

    /// <summary>The node <paramref name="capacitor"/> takes its charge through, if it is in the same island; else none.</summary>
    private static Capacitor? UpstreamIn(Capacitor capacitor, PowerIsland island) =>
        capacitor.Parent is Capacitor parent && ReferenceEquals(parent.Island, island) ? parent : null;

    /// <summary>
    /// The node of the island <paramref name="point"/>'s power route passes first on its way to the base: the nearest
    /// point up the route that is one of <paramref name="island"/>'s nodes. Null when it takes its charge from the base
    /// directly (or through a rival's nodes only).
    /// </summary>
    private static Capacitor? UpstreamNode(LinkPoint point, Dictionary<LinkPoint, Capacitor> nodes,
        Dictionary<Capacitor, PowerIsland> targets, PowerIsland island)
    {
        var at = point;
        for (int remaining = point.Hops; remaining > 1 && at.UpstreamLink is Link link; remaining--)
        {
            at = OtherEnd(link, at);
            if (nodes.TryGetValue(at, out var node) && targets.TryGetValue(node, out var its) && ReferenceEquals(its, island))
                return node;
        }
        return null;
    }

    /// <summary>The capacitor of the node whose coverage a harvester is in; null for the base itself.</summary>
    private Capacitor? CoveringNode(Capacitor capacitor, TankCoverage coverage, MachineList machines, List<Machine> near)
    {
        if (coverage.Point is not LinkPoint point || point.Type != LinkPointType.Machine)
            return null;
        if (capacitor.Parent is { } parent && ReferenceEquals(parent.Machine.LinkPoint, point))
            return parent;
        machines.CollectNear(coverage.Node, 1, near);
        foreach (var node in near)
            if (ReferenceEquals(node.LinkPoint, point))
                return Find(node);
        return null;
    }

    /// <summary>
    /// Works out how much each filling capacitor of <paramref name="island"/> may take a tick: the routes of the filling
    /// capacitors are collected, demand is added up from the deepest node, each node passes on at most its limit (the
    /// higher tiers first, a tier's share split in proportion), and the scale that leaves is handed back down.
    /// </summary>
    private void RebuildTree(PowerIsland island)
    {
        island.TreeDirty = false;
        int stamp = ++_treeStamp;
        var nodes = _treeNodes;
        foreach (var filling in island.Filling)
        {
            // Up to the first node already collected (or the top), then the depths are numbered on the way down.
            _treeRoute.Clear();
            Capacitor? node = filling;
            while (node != null && node.TreeStamp != stamp && _treeRoute.Count < _capacitors.Count)
            {
                node.TreeStamp = stamp;
                _treeRoute.Add(node);
                node = UpstreamIn(node, island);
            }
            int depth = node == null ? 0 : node.Depth + 1;
            for (int i = _treeRoute.Count - 1; i >= 0; i--)
            {
                var added = _treeRoute[i];
                added.Depth = depth++;
                Array.Clear(added.TreeAsk);
                nodes.Add(added);
            }
        }
        _treeRoute.Clear();
        nodes.Sort(DeepestFirst);

        // From the leaves: what reaches a node from below (TreeAsk) is capped at its limit, its own top-up is added, and
        // that is what it asks of the node above.
        foreach (var node in nodes)
        {
            double left = LimitOf(node);
            for (int tier = 0; tier < TierCount; tier++)
            {
                double asked = node.TreeAsk[tier];
                double passed = Math.Min(asked, left);
                left -= passed;
                node.TreeRatio[tier] = asked > 0.0 ? passed / asked : 1.0;
                node.TreeAsk[tier] = passed;
            }
            if (node.IsFilling)
                node.TreeAsk[TierOf(node)] += node.Machine.Behavior.Capacitor.TransferPerTick;
            if (UpstreamIn(node, island) is Capacitor up)
                for (int tier = 0; tier < TierCount; tier++)
                    up.TreeAsk[tier] += node.TreeAsk[tier];
        }

        // From the top: each node keeps the share its ancestors pass on, and pays the loss of each on the way.
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            var node = nodes[i];
            if (UpstreamIn(node, island) is Capacitor up)
            {
                for (int tier = 0; tier < TierCount; tier++)
                    node.TreeScale[tier] = up.TreeScale[tier] * up.TreeRatio[tier];
                node.LossMultiplier = up.LossMultiplier * (1.0 + Tweaks.Throughput.LossFraction);
            }
            else
            {
                Array.Fill(node.TreeScale, 1.0);
                node.LossMultiplier = 1.0;
            }
            if (node.IsFilling)
                node.Allowance = node.Machine.Behavior.Capacitor.TransferPerTick * node.TreeScale[TierOf(node)];
        }
        nodes.Clear();
    }

    /// <summary>
    /// <paramref name="given"/> reached <paramref name="capacitor"/> and the island drew <paramref name="drawn"/> for it:
    /// each node on the way books what it passed on and keeps its loss as pending heat, the top one the rest of the draw.
    /// </summary>
    private static void PassThrough(Capacitor capacitor, PowerIsland island, double given, double drawn, int tick)
    {
        double carried = given;
        Capacitor? node = UpstreamIn(capacitor, island);
        if (node == null)
        {
            // Every change of parent or island marks the tree to be worked out again before the next top-up, so this
            // is not reached; if it were, the loss stays with the machine, as heat beside it, and nothing is lost.
            Debug.Assert(false, "a capacitor drew a loss with no node on its way: its tree is stale");
            capacitor.Pending += drawn - given;
            return;
        }
        while (node != null)
        {
            var up = UpstreamIn(node, island);
            double loss = up == null ? drawn - carried : carried * Tweaks.Throughput.LossFraction;
            node.Pending += loss;
            node.Passed.Add(tick, 0.0, carried);
            node.HasCarried = true;
            carried += loss;
            node = up;
        }
    }

    /// <summary>
    /// At its settling (before the heat of its last period lands), a node that carried charge since the last one, or
    /// that is still hot, is throttled or overheated by the temperature of its ground (with a hysteresis on the way
    /// back). An overheated one that carried charge loses health, recorded as <see cref="StepEventKind.MachineDamaged"/>
    /// (or <see cref="StepEventKind.MachineWrecked"/>) like a hit. A cool node that carried nothing is not looked at.
    /// </summary>
    private static void CheckHeat(Capacitor node, ITerrainAccess terrain, StepEventLog? events)
    {
        bool carried = node.HasCarried;
        node.HasCarried = false;
        if (!carried && node.Heat == ThroughputHeat.Normal)
            return;
        // SampleAverageHeat is normalised (temperature / 255).
        float temperature = terrain.SampleAverageHeat(node.Machine.Position, Tweaks.Energy.IdleHeatRadius) * 255f;
        var heat = temperature >= Tweaks.Throughput.OverheatTemperature ? ThroughputHeat.Overheated
            : temperature >= Tweaks.Throughput.ThrottleTemperature ? ThroughputHeat.Throttled
            : node.Heat != ThroughputHeat.Normal &&
              temperature >= Tweaks.Throughput.ThrottleTemperature - Tweaks.Throughput.ThrottleHysteresis
                ? ThroughputHeat.Throttled
            : ThroughputHeat.Normal;
        var machine = node.Machine;
        if (heat == ThroughputHeat.Overheated && carried && machine.IsStanding)
        {
            bool wrecked = machine.TakeDamage(Tweaks.Throughput.OverheatDamage);
            events?.Record(new StepEvent(wrecked ? StepEventKind.MachineWrecked : StepEventKind.MachineDamaged,
                machine.Position, machine.OwnerColor, machine.Type));
        }
        if (heat == node.Heat)
            return;
        node.Heat = heat;
        MarkTreeDirty(node.Island);
    }

    private static ulong MixThroughput(ulong hash, Capacitor capacitor)
    {
        hash = Fnv1a.Mix(hash, capacitor.Parent?.Machine.Sequence ?? -1);
        hash = Fnv1a.Mix(hash, capacitor.ThroughputScale);
        hash = Fnv1a.Mix(hash, (int)capacitor.Heat);
        hash = Fnv1a.Mix(hash, capacitor.HasCarried);
        hash = Fnv1a.Mix(hash, capacitor.Allowance);
        hash = Fnv1a.Mix(hash, capacitor.LossMultiplier);
        return hash;
    }

    private sealed partial class Capacitor
    {
        // The node it takes its charge through (null: from the island's root). Only a parent in the same island counts.
        public Capacitor? Parent;
        // Its Conduit multiple of the node limit.
        public double ThroughputScale = 1.0;
        public ThroughputHeat Heat;
        // It passed charge on since its last heat check.
        public bool HasCarried;
        // While filling: the most it takes a tick, and the charge drawn for each unit that reaches it (losses on the way).
        public double Allowance;
        public double LossMultiplier = 1.0;
        // Charge it passed on to the machines behind it.
        public FlowWindow Passed;
        // Scratch of RebuildTree, written before it is read there: its depth below the root, the rebuild that last
        // collected it, what is asked through it by tier, the share of that it passes and the share left to it from above.
        public int Depth;
        public int TreeStamp;
        public double[] TreeAsk = new double[TierCount];
        public double[] TreeRatio = new double[TierCount];
        public double[] TreeScale = new double[TierCount];
    }

    private sealed partial class PowerIsland
    {
        // Its tree must be worked out again before its next top-up.
        public bool TreeDirty = true;
    }
}
