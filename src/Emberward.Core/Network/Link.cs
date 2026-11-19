namespace Emberward.Core.Network;

using Emberward.Core.Types;
using Emberward.Core.Config;
using Emberward.Core.Collision;
using Emberward.Core.Terrain;

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

    /// <summary>
    /// Ticks left of the ore flow last seen on this link (ore a tank unloaded into the network, on its way home): set
    /// to <c>Ore.LinkFlowShowTicks</c> when a unit passes and counted down every tick, so the drawn flow is steady
    /// between units.
    /// </summary>
    public int OreFlowTicks { get; set; }

    /// <summary>True when that ore flows from <see cref="From"/> to <see cref="To"/>.</summary>
    public bool OreFlowsFromTo { get; set; }

    /// <summary>Its index in <see cref="LinkMap.Links"/>.</summary>
    internal int Order { get; set; }

    /// <summary>Scratch for the map: already on the list of links to look at again.</summary>
    internal bool IsQueued { get; set; }

    /// <summary>Forgets every flow, as on a link that was just made.</summary>
    internal void ForgetFlows()
    {
        HeatFlow = 0.0;
        FlowsFromTo = false;
        ChargeFlow = 0.0;
        ChargeFlowsFromTo = false;
        OreFlowTicks = 0;
        OreFlowsFromTo = false;
    }

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
