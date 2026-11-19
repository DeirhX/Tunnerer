namespace Emberward.Core.Network;

using Emberward.Core.Snapshots;
using Emberward.Core.Types;
using Emberward.Core.Config;
using Emberward.Core.Collision;
using Emberward.Core.Terrain;
using Emberward.Core.Rendering;

/// <summary>
/// The link network: the points (bases and planted link nodes), the links between points near enough to link, and
/// which points have power, found from the bases outward over the links with a clear line of sight.
/// <para>
/// The map is brought up to date every <c>RefreshLinkMapInterval</c>, and at once when a machine's point is switched
/// off. It keeps its links from one refresh to the next and does only what has changed: a new point gets its links,
/// a point switched off loses them, and a link's line of sight is looked at again only when a cell near its line
/// started or stopped blocking (<see cref="SightlineChanges"/>). Power is searched again only when one of those
/// changed something. The result is the one a rebuild from nothing would give.
/// </para>
/// </summary>
public class LinkMap : ISnapshotLoaded
{
    // Side of the squares links are filed in by the cells their line crosses, as a power of two.
    private const int TileShift = 5;

    // The order of Links: by the registration order of the first end, then of the second.
    private static readonly Comparison<Link> ByEnds = static (a, b) => a.From.Sequence != b.From.Sequence
        ? a.From.Sequence.CompareTo(b.From.Sequence)
        : a.To.Sequence.CompareTo(b.To.Sequence);
    private static readonly Comparison<Link> ByOrder = static (a, b) => a.Order.CompareTo(b.Order);

    private readonly List<LinkPoint> _points = new();
    private readonly List<Link> _links = new();
    private readonly int _idSeed;
    private int _nextPointIndex;
    private TimeSpan _elapsed;
    private TimeSpan _nextRelink;

    // What has happened since the last refresh.
    private readonly List<LinkPoint> _unlinked = new();
    private bool _hasDisabledPoint;
    private bool _hasSpreadPower;

    // Looked up by key only, never enumerated, so its order cannot reach the simulation.
    [NotSaved("rebuilt on load from the link list (OnSnapshotLoaded); looked up by key only")]
    private readonly Dictionary<long, List<Link>> _linksByTile = new();
    private readonly List<Link> _toRecheck = new();
    private readonly List<Link> _candidates = new();
    private List<LinkPoint> _frontier = new();
    private List<LinkPoint> _nextFrontier = new();

    public IReadOnlyList<LinkPoint> Points => _points;
    public IReadOnlyList<Link> Links => _links;
    public int PointCount => _points.Count;
    public int LinkCount => _links.Count;

    /// <summary>
    /// Counts the power searches: it changes whenever which points have power, or the route home of any, may have
    /// changed. The energy network looks at its islands again only then.
    /// </summary>
    public int PowerVersion { get; private set; }

    /// <summary>Links whose line of sight the last <see cref="Advance"/> looked at: what the step cost.</summary>
    public int SightlinesCheckedInLastAdvance { get; private set; }

    public LinkMap(int idSeed = 0)
    {
        _idSeed = idSeed != 0 ? idSeed : 0x6E624EB7;
    }

    public LinkPoint RegisterPoint(Position position, LinkPointType type)
    {
        int index = _nextPointIndex++;
        var point = new LinkPoint(position, type, GeneratePointId(index)) { Map = this, Sequence = index };
        _points.Add(point);
        _unlinked.Add(point);
        return point;
    }

    /// <summary>Mixes the map's counters and clocks, which the link list itself does not show, for the sync digest.</summary>
    internal ulong MixLatent(ulong hash)
    {
        hash = Fnv1a.Mix(hash, _nextPointIndex);
        hash = Fnv1a.Mix(hash, _elapsed);
        hash = Fnv1a.Mix(hash, _nextRelink);
        hash = Fnv1a.Mix(hash, _hasDisabledPoint);
        return Fnv1a.Mix(hash, _hasSpreadPower);
    }

    public void Advance(TerrainGrid terrain, TimeSpan dt)
    {
        SightlinesCheckedInLastAdvance = 0;
        _elapsed += dt;
        if (_elapsed >= _nextRelink)
            _nextRelink += Tweaks.World.RefreshLinkMapInterval;
        else if (!_hasDisabledPoint)
            return; // Destroyed points disconnect the network this tick.

        Refresh(terrain);
    }

    internal void NoteEnabledChanged(LinkPoint point)
    {
        if (point.IsEnabled)
        {
            _unlinked.Add(point);
            return;
        }
        _hasDisabledPoint = true;
    }

    private int GeneratePointId(int pointIndex)
    {
        uint mixed = (uint)_idSeed ^ (uint)(pointIndex + 1) * 0x9e3779b9u;
        return unchecked((int)FastRandom.Hash32(mixed));
    }

    private void Refresh(TerrainGrid terrain)
    {
        SightlineChanges sightlines = terrain.SightlineChanges;
        if (!sightlines.IsFollowed)
            sightlines.Follow();

        bool pointsChanged = DropDisabledPoints();
        pointsChanged |= LinkNewPoints(terrain);
        if (pointsChanged)
            PutLinksInOrder();
        bool sightChanged = RecheckSightlines(terrain, sightlines);

        // A link that is not live carries nothing, and one that carried nothing starts over: as if made anew.
        foreach (var link in _links)
            if (link.Type != LinkType.Live || !(link.HeatFlow > 0.0 || link.ChargeFlow > 0.0 || link.OreFlowTicks > 0))
                link.ForgetFlows();

        if (pointsChanged || sightChanged || !_hasSpreadPower)
            SpreadPower();
    }

    /// <summary>Drops the links of points that were switched off, and the points themselves unless they are bases.</summary>
    private bool DropDisabledPoints()
    {
        if (!_hasDisabledPoint)
            return false;
        _hasDisabledPoint = false;

        int kept = 0;
        for (int i = 0; i < _links.Count; i++)
        {
            Link link = _links[i];
            if (link.From.IsEnabled && link.To.IsEnabled)
                _links[kept++] = link;
            else
                Unfile(link);
        }
        _links.RemoveRange(kept, _links.Count - kept);

        foreach (var point in _points)
            if (!point.IsEnabled)
                point.IsLinked = false;
        _points.RemoveAll(p => !p.IsEnabled && p.Type != LinkPointType.Base);
        return true;
    }

    /// <summary>Makes the links of the points registered since the last refresh, to every point within reach.</summary>
    private bool LinkNewPoints(TerrainGrid terrain)
    {
        if (_unlinked.Count == 0)
            return false;
        foreach (var point in _unlinked)
        {
            if (point.IsLinked || !point.IsEnabled)
                continue;
            foreach (var other in _points)
            {
                if (!other.IsLinked)
                    continue;
                // The earlier registered point is the link's first end.
                LinkPoint from = other.Sequence < point.Sequence ? other : point;
                LinkPoint to = ReferenceEquals(from, other) ? point : other;
                float dist = VectorF.FromPositions(from.Position, to.Position).Length;
                if (dist > Tweaks.World.MaximumTheoreticalLinkDistance)
                    continue;

                var link = new Link(from, to);
                link.UpdateType(terrain);
                SightlinesCheckedInLastAdvance++;
                File(link);
                _links.Add(link);
            }
            point.IsLinked = true;
        }
        _unlinked.Clear();
        return true;
    }

    private void PutLinksInOrder()
    {
        _links.Sort(ByEnds);
        foreach (var point in _points)
            point.Links.Clear();
        for (int i = 0; i < _links.Count; i++)
        {
            Link link = _links[i];
            link.Order = i;
            link.From.Links.Add(link);
            link.To.Links.Add(link);
        }
    }

    /// <summary>
    /// Looks again at the line of sight of every link filed near a cell that started or stopped blocking. True when
    /// a link changed type.
    /// </summary>
    private bool RecheckSightlines(TerrainGrid terrain, SightlineChanges sightlines)
    {
        bool changed = false;
        if (sightlines.AllChanged)
        {
            foreach (var link in _links)
                changed |= Recheck(link, terrain);
        }
        else
        {
            int width = terrain.Width;
            long lastKey = long.MinValue;
            foreach (int cell in sightlines.Cells)
            {
                // Cells come in runs (a dig, a blast), so most repeat the tile before them.
                long key = TileKey(cell % width, cell / width);
                if (key == lastKey)
                    continue;
                lastKey = key;
                if (!_linksByTile.TryGetValue(key, out var filed))
                    continue;
                foreach (var link in filed)
                {
                    if (link.IsQueued)
                        continue;
                    link.IsQueued = true;
                    _toRecheck.Add(link);
                }
            }
            foreach (var link in _toRecheck)
            {
                link.IsQueued = false;
                changed |= Recheck(link, terrain);
            }
            _toRecheck.Clear();
        }
        sightlines.Clear();
        return changed;
    }

    private bool Recheck(Link link, TerrainGrid terrain)
    {
        SightlinesCheckedInLastAdvance++;
        LinkType was = link.Type;
        link.UpdateType(terrain);
        return link.Type != was;
    }

    /// <summary>Files a link under every tile its line crosses. A link too long to ever be live is not filed.</summary>
    private void File(Link link)
    {
        if (link.Distance > Tweaks.World.MaximumLiveLinkDistance)
            return;
        long lastKey = long.MinValue;
        Raycaster.BresenhamLine(link.From.Position, link.To.Position, cell =>
        {
            long key = TileKey(cell.X, cell.Y);
            if (key == lastKey)
                return;
            lastKey = key;
            if (!_linksByTile.TryGetValue(key, out var filed))
                _linksByTile[key] = filed = new List<Link>();
            filed.Add(link);
        });
    }

    void ISnapshotLoaded.OnSnapshotLoaded()
    {
        _linksByTile.Clear();
        foreach (Link link in _links)
            File(link);
    }

    private void Unfile(Link link)
    {
        if (link.Distance > Tweaks.World.MaximumLiveLinkDistance)
            return;
        long lastKey = long.MinValue;
        Raycaster.BresenhamLine(link.From.Position, link.To.Position, cell =>
        {
            long key = TileKey(cell.X, cell.Y);
            if (key == lastKey)
                return;
            lastKey = key;
            if (_linksByTile.TryGetValue(key, out var filed))
                filed.Remove(link);
        });
    }

    private static long TileKey(int x, int y) => ((long)(y >> TileShift) << 32) | (uint)(x >> TileShift);

    /// <summary>
    /// Finds which points have power: from the bases outward over live links, a round per hop, so each point's
    /// upstream link is on a shortest route home. Within a round the links are taken in the order of
    /// <see cref="Links"/>, and the first to reach a point becomes its upstream link.
    /// </summary>
    private void SpreadPower()
    {
        _hasSpreadPower = true;
        PowerVersion++;
        _frontier.Clear();
        foreach (var p in _points)
        {
            p.IsPowered = p.IsEnabled && p.Type == LinkPointType.Base;
            p.UpstreamLink = null;
            p.Hops = p.IsPowered ? 0 : -1;
            p.PoweredInRound = p.IsPowered ? 1 : 0;
            if (p.IsPowered)
                _frontier.Add(p);
        }

        for (int round = 1; _frontier.Count > 0; round++)
        {
            // Only links that end at a point powered in the last round can carry power further.
            _candidates.Clear();
            foreach (var point in _frontier)
                foreach (var link in point.Links)
                    if (link.Type == LinkType.Live)
                        _candidates.Add(link);
            _candidates.Sort(ByOrder);

            _nextFrontier.Clear();
            foreach (var link in _candidates)
            {
                if (link.From.IsPowered && !link.To.IsPowered && link.From.PoweredInRound == round)
                    Power(link.To, link, link.From, round + 1);
                else if (link.To.IsPowered && !link.From.IsPowered && link.To.PoweredInRound == round)
                    Power(link.From, link, link.To, round + 1);
            }
            (_frontier, _nextFrontier) = (_nextFrontier, _frontier);
        }

        void Power(LinkPoint point, Link through, LinkPoint from, int round)
        {
            point.IsPowered = true;
            point.UpstreamLink = through;
            point.Hops = from.Hops + 1;
            point.PoweredInRound = round;
            _nextFrontier.Add(point);
        }
    }

    /// <summary>Forgets last tick's heat flows; called before machines act.</summary>
    public void ClearHeatFlow()
    {
        foreach (var link in _links)
            link.HeatFlow = 0.0;
    }

    /// <summary>Forgets the charge flows; the world re-books every pylon's last hand-out after machines act.</summary>
    public void ClearChargeFlow()
    {
        foreach (var link in _links)
            link.ChargeFlow = 0.0;
    }

    /// <summary>Records <paramref name="charge"/> travelling from the base out to the pylon at <paramref name="pylon"/>.</summary>
    public static void CarryCharge(LinkPoint? pylon, double charge)
    {
        if (pylon == null || charge <= 0.0)
            return;
        var point = pylon;
        for (int hops = 0; point.UpstreamLink is { } link && hops < 64; hops++)
        {
            // Charge flows toward the pylon, so into `point` from the link's other end.
            link.ChargeFlowsFromTo = !ReferenceEquals(link.From, point);
            link.ChargeFlow += charge;
            point = link.ChargeFlowsFromTo ? link.From : link.To;
        }
    }

    /// <summary>Counts down the ore flows (<see cref="Link.OreFlowTicks"/>); called once a tick before tanks act.</summary>
    public void AgeOreFlow()
    {
        foreach (var link in _links)
            if (link.OreFlowTicks > 0)
                link.OreFlowTicks--;
    }

    /// <summary>Records ore a tank unloaded at <paramref name="node"/> travelling along the links back to its base.</summary>
    public static void CarryOre(LinkPoint? node)
    {
        var point = node;
        for (int hops = 0; point?.UpstreamLink is { } link && hops < 64; hops++)
        {
            link.OreFlowsFromTo = ReferenceEquals(link.From, point);
            link.OreFlowTicks = Tweaks.Ore.LinkFlowShowTicks;
            point = link.OreFlowsFromTo ? link.To : link.From;
        }
    }

    /// <summary>Records <paramref name="energy"/> travelling from <paramref name="source"/> back to its base.</summary>
    public static void CarryHeat(LinkPoint source, double energy)
    {
        if (energy <= 0.0)
            return;
        var point = source;
        for (int hops = 0; point.UpstreamLink is { } link && hops < 64; hops++)
        {
            link.FlowsFromTo = ReferenceEquals(link.From, point);
            link.HeatFlow += energy;
            point = link.FlowsFromTo ? link.To : link.From;
        }
    }

    /// <summary>
    /// Predicts whether a relay planted at <paramref name="at"/> would join a powered network: the nearest powered
    /// point in live range with a clear line of sight wins; otherwise the nearest powered point in range is
    /// reported with the first terrain pixel that blocks it; otherwise the nearest powered point at any range.
    /// </summary>
    public LinkPreview PreviewLink(Position at, ITerrainPixelReadOnly terrain)
    {
        LinkPoint? bestLive = null, bestBlocked = null, nearest = null;
        float bestLiveDist = float.MaxValue, bestBlockedDist = float.MaxValue, nearestDist = float.MaxValue;
        Position blockedAt = default;

        foreach (var point in _points)
        {
            if (!point.IsEnabled || !point.IsPowered)
                continue;
            float dist = VectorF.FromPositions(at, point.Position).Length;
            if (dist < nearestDist) { nearestDist = dist; nearest = point; }
            if (dist > Tweaks.World.MaximumLiveLinkDistance)
                continue;

            bool blocked = Raycaster.TryFindLineBlock(terrain, at, point.Position, out Position firstBlock);
            if (!blocked && dist < bestLiveDist) { bestLiveDist = dist; bestLive = point; }
            else if (blocked && dist < bestBlockedDist) { bestBlockedDist = dist; bestBlocked = point; blockedAt = firstBlock; }
        }

        if (bestLive != null)
            return new LinkPreview(LinkPreviewStatus.Live, bestLive.Position, default);
        if (bestBlocked != null)
            return new LinkPreview(LinkPreviewStatus.Blocked, bestBlocked.Position, blockedAt);
        if (nearest != null)
            return new LinkPreview(LinkPreviewStatus.TooFar, nearest.Position, default);
        return new LinkPreview(LinkPreviewStatus.NoNetwork, default, default);
    }

    public int CopyRenderStates(Span<LinkRenderState> destination)
    {
        int count = Math.Min(destination.Length, _links.Count);
        for (int i = 0; i < count; i++)
        {
            var link = _links[i];
            destination[i] = new LinkRenderState(
                From: link.From.Position,
                To: link.To.Position,
                Type: link.Type,
                IsAlive: link.IsAlive,
                HeatFlow: (float)(link.HeatFlow / Tweaks.Machine.RelayHeatPumpPerTick),
                FlowsFromTo: link.FlowsFromTo,
                ChargeFlow: (float)(link.ChargeFlow / Tweaks.Machine.PylonChargePerAction),
                ChargeFlowsFromTo: link.ChargeFlowsFromTo,
                OreFlow: link.OreFlowTicks / (float)Tweaks.Ore.LinkFlowShowTicks,
                OreFlowsFromTo: link.OreFlowsFromTo);
        }
        return count;
    }
}

public enum LinkPreviewStatus { NoNetwork, TooFar, Blocked, Live }

/// <summary>Result of <see cref="LinkMap.PreviewLink"/>: where a new relay would connect, or what stops it.</summary>
public readonly record struct LinkPreview(LinkPreviewStatus Status, Position Target, Position BlockedAt);
