namespace Tunnerer.Core.Entities.Links;

using Tunnerer.Core.Types;
using Tunnerer.Core.Config;
using Tunnerer.Core.Collision;
using Tunnerer.Core.Terrain;

public enum LinkType { Live, Blocked, Theoretical }

public class Link
{
    public LinkPoint From { get; }
    public LinkPoint To { get; }
    public LinkType Type { get; set; } = LinkType.Theoretical;
    public bool IsAlive { get; set; } = true;

    /// <summary>Heat energy carried along this link during the last tick (drawn as flowing heat).</summary>
    public double HeatFlow { get; set; }

    /// <summary>True when that heat flows from <see cref="From"/> to <see cref="To"/>; false for To to From.</summary>
    public bool FlowsFromTo { get; set; }

    /// <summary>Charge carried out to pylons along this link during the last pylon actions (drawn as flowing charge).</summary>
    public double ChargeFlow { get; set; }

    /// <summary>True when that charge flows from <see cref="From"/> to <see cref="To"/>.</summary>
    public bool ChargeFlowsFromTo { get; set; }

    public Link(LinkPoint from, LinkPoint to)
    {
        From = from;
        To = to;
    }

    public float Distance => VectorF.FromPositions(From.Position, To.Position).Length;

    public void UpdateType(ITerrainPixelReadOnly terrain)
    {
        if (!From.IsEnabled || !To.IsEnabled || Distance > Tweaks.World.MaximumLiveLinkDistance)
        {
            Type = LinkType.Theoretical;
            return;
        }

        Type = Raycaster.HasOpenLine(terrain, From.Position, To.Position) ? LinkType.Live : LinkType.Blocked;
    }
}
