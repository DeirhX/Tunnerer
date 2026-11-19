namespace Emberward.Core.Network;

using Emberward.Core.Types;

public enum LinkPointType { Base, Machine, Transit, Controllable }

public class LinkPoint
{
    private bool _isEnabled = true;

    public Position Position { get; }
    public LinkPointType Type { get; }

    /// <summary>False once the point is switched off (its machine was destroyed): the map drops it and its links.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (value == _isEnabled)
                return;
            _isEnabled = value;
            Map?.NoteEnabledChanged(this);
        }
    }

    public bool IsPowered { get; set; }
    public int Id { get; }

    /// <summary>
    /// The live link this point's power comes through, toward the nearest base (in hops); null for bases and for
    /// unpowered points. Heat a relay pumps travels back along these links to the base.
    /// </summary>
    public Link? UpstreamLink { get; set; }

    /// <summary>Live links between this point and the nearest base: 0 for bases, -1 while unpowered.</summary>
    public int Hops { get; set; } = -1;

    /// <summary>Whether this point's upstream route ends at <paramref name="source"/>.</summary>
    internal bool IsPoweredBy(LinkPoint? source)
    {
        if (!IsPowered || source is null)
            return false;

        var point = this;
        for (int remaining = Hops; remaining > 0 && point.UpstreamLink is Link link; remaining--)
            point = ReferenceEquals(link.From, point) ? link.To : link.From;
        return ReferenceEquals(point, source);
    }

    /// <summary>The map this point is registered in.</summary>
    internal LinkMap? Map { get; set; }

    /// <summary>Its place in the order points were registered in, which is the order of <see cref="LinkMap.Points"/>.</summary>
    internal int Sequence { get; set; }

    /// <summary>The links that end here, in the order of <see cref="LinkMap.Links"/>.</summary>
    internal List<Link> Links { get; } = new();

    /// <summary>True once the map has made this point's links to the points that had theirs.</summary>
    internal bool IsLinked { get; set; }

    /// <summary>Scratch for the power search: the round in which power reached this point.</summary>
    internal int PoweredInRound { get; set; }

    public LinkPoint(Position position, LinkPointType type, int id)
    {
        Position = position;
        Type = type;
        Id = id;
        IsPowered = type == LinkPointType.Base;
    }
}
