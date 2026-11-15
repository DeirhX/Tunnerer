namespace Tunnerer.Core.Entities.Links;

using Tunnerer.Core.Types;

public enum LinkPointType { Base, Machine, Transit, Controllable }

public class LinkPoint
{
    public Position Position { get; set; }
    public LinkPointType Type { get; }
    public bool IsEnabled { get; set; } = true;
    public bool IsPowered { get; set; }
    public int Id { get; }

    /// <summary>
    /// The live link this point's power comes through, toward the nearest base (in hops); null for bases and for
    /// unpowered points. Heat a relay pumps travels back along these links to the base.
    /// </summary>
    public Link? UpstreamLink { get; set; }

    /// <summary>Live links between this point and the nearest base: 0 for bases, -1 while unpowered.</summary>
    public int Hops { get; set; } = -1;

    public LinkPoint(Position position, LinkPointType type, int id)
    {
        Position = position;
        Type = type;
        Id = id;
        IsPowered = type == LinkPointType.Base;
    }
}
