namespace Tunnerer.Core.Entities.Links;

using Tunnerer.Core.Types;
using Tunnerer.Core.Config;
using Tunnerer.Core.Collision;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Rendering;

public class LinkMap
{
    private readonly List<LinkPoint> _points = new();
    private readonly List<Link> _links = new();
    private readonly bool _deterministicSimulation;
    private readonly int _idSeed;
    private int _nextPointIndex;
    private TimeSpan _elapsed;
    private TimeSpan _nextRelink;

    public IReadOnlyList<LinkPoint> Points => _points;
    public IReadOnlyList<Link> Links => _links;
    public int PointCount => _points.Count;
    public int LinkCount => _links.Count;

    public LinkMap(bool deterministicSimulation = false, int idSeed = 0)
    {
        _deterministicSimulation = deterministicSimulation;
        _idSeed = idSeed != 0 ? idSeed : 0x6E624EB7;
    }

    public LinkPoint RegisterPoint(Position position, LinkPointType type)
    {
        int id = _deterministicSimulation
            ? GenerateDeterministicId(_nextPointIndex++)
            : unchecked(_idSeed ^ ++_nextPointIndex);
        var point = new LinkPoint(position, type, id);
        _points.Add(point);
        return point;
    }

    public void RemoveAll()
    {
        _points.Clear();
        _links.Clear();
        _nextPointIndex = 0;
        _elapsed = TimeSpan.Zero;
        _nextRelink = TimeSpan.Zero;
    }

    public void Advance(ITerrainPixelReadOnly terrain, TimeSpan dt)
    {
        _elapsed += dt;
        if (_elapsed < _nextRelink) return;
        _nextRelink += Tweaks.World.RefreshLinkMapInterval;

        RebuildLinks(terrain);
    }

    private int GenerateDeterministicId(int pointIndex)
    {
        uint mixed = (uint)_idSeed ^ (uint)(pointIndex + 1) * 0x9e3779b9u;
        return unchecked((int)FastRandom.Hash32(mixed));
    }

    private void RebuildLinks(ITerrainPixelReadOnly terrain)
    {
        // Machines already booked this tick's heat on the old links; keep it on their replacements so the drawn
        // heat flow does not drop to zero for a tick at every rebuild.
        var previousFlow = new Dictionary<(int, int), Link>();
        foreach (var old in _links)
            if (old.HeatFlow > 0.0 || old.ChargeFlow > 0.0)
                previousFlow[(old.From.Id, old.To.Id)] = old;
        _links.Clear();
        _points.RemoveAll(p => !p.IsEnabled && p.Type != LinkPointType.Base);

        for (int i = 0; i < _points.Count; i++)
        {
            if (!_points[i].IsEnabled) continue;
            for (int j = i + 1; j < _points.Count; j++)
            {
                if (!_points[j].IsEnabled) continue;
                float dist = VectorF.FromPositions(_points[i].Position, _points[j].Position).Length;
                if (dist > Tweaks.World.MaximumTheoreticalLinkDistance) continue;

                var link = new Link(_points[i], _points[j]);
                link.UpdateType(terrain);
                if (link.Type == LinkType.Live && previousFlow.TryGetValue((link.From.Id, link.To.Id), out var old))
                {
                    link.HeatFlow = old.HeatFlow;
                    link.FlowsFromTo = old.FlowsFromTo;
                    link.ChargeFlow = old.ChargeFlow;
                    link.ChargeFlowsFromTo = old.ChargeFlowsFromTo;
                }
                _links.Add(link);
            }
        }

        // Propagate power from bases, breadth first so each point's upstream link is on a shortest route home.
        var frontier = new List<LinkPoint>();
        foreach (var p in _points)
        {
            p.IsPowered = p.Type == LinkPointType.Base;
            p.UpstreamLink = null;
            if (p.IsPowered)
                frontier.Add(p);
        }

        while (frontier.Count > 0)
        {
            var next = new List<LinkPoint>();
            foreach (var link in _links)
            {
                if (link.Type != LinkType.Live) continue;
                if (link.From.IsPowered && !link.To.IsPowered && frontier.Contains(link.From))
                {
                    link.To.IsPowered = true;
                    link.To.UpstreamLink = link;
                    next.Add(link.To);
                }
                else if (link.To.IsPowered && !link.From.IsPowered && frontier.Contains(link.To))
                {
                    link.From.IsPowered = true;
                    link.From.UpstreamLink = link;
                    next.Add(link.From);
                }
            }
            frontier = next;
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

    public void Draw(Surface surface)
    {
        var liveColor = Tweaks.Colors.LinkLive.ToArgb();
        var blockedColor = Tweaks.Colors.LinkBlocked.ToArgb();

        foreach (var link in _links)
        {
            if (link.Type == LinkType.Theoretical) continue;

            uint color = link.Type == LinkType.Live ? liveColor : blockedColor;

            Raycaster.BresenhamLine(link.From.Position, link.To.Position, pos =>
            {
                if (surface.IsInside(pos.X, pos.Y))
                    surface.Pixels[pos.X + pos.Y * surface.Width] = color;
            });
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

            Position firstBlock = default;
            bool blocked = Raycaster.BresenhamLineAny(at, point.Position, pos =>
            {
                if (!Pixel.BlocksLink(terrain.GetPixel(pos)))
                    return false;
                firstBlock = pos;
                return true;
            });
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
                ChargeFlowsFromTo: link.ChargeFlowsFromTo);
        }
        return count;
    }
}

public enum LinkPreviewStatus { NoNetwork, TooFar, Blocked, Live }

/// <summary>Result of <see cref="LinkMap.PreviewLink"/>: where a new relay would connect, or what stops it.</summary>
public readonly record struct LinkPreview(LinkPreviewStatus Status, Position Target, Position BlockedAt);
