namespace Tunnerer.Desktop.Rendering.Entities;

using Tunnerer.Core;
using Tunnerer.Core.Entities.Links;
using Tunnerer.Core.Rendering;
using Tunnerer.Core.Types;

/// <summary>Network links as beams: heat flowing to the base, and charge flowing out to pylons and batteries.</summary>
public sealed class LinkQuads
{
    private LinkRenderState[] _links = [];
    private int _linkCount;

    public void Capture(World world)
    {
        if (_links.Length < world.LinkMap.LinkCount)
            _links = new LinkRenderState[Math.Max(world.LinkMap.LinkCount, 32)];
        _linkCount = world.LinkMap.CopyRenderStates(_links);
    }

    /// <summary>
    /// Live and blocked links as beams, handed to <paramref name="beams"/>, which fades them in and out. A link
    /// carrying heat is oriented so its +x runs toward the base, which is the way the shader's heat pulses flow.
    /// </summary>
    public void Submit(BeamFader beams)
    {
        for (int i = 0; i < _linkCount; i++)
        {
            ref readonly LinkRenderState link = ref _links[i];
            if (!link.IsAlive || link.Type == LinkType.Theoretical)
                continue;

            // Keyed by the link's own ends, so a flow that changes direction does not break and regrow the beam.
            Position from = link.FlowsFromTo ? link.From : link.To;
            Position to = link.FlowsFromTo ? link.To : link.From;
            bool live = link.Type == LinkType.Live;
            float heat = live ? link.HeatFlow : 0f;
            // Room for the glow, which the shader grows with the flow.
            float halfWidth = heat > 0.01f ? 4.5f : 1.2f;
            beams.Submit(new BeamKey(EntityShape.KindLink, link.From, link.To),
                from.X, from.Y, to.X, to.Y, heat, halfWidth, blocked: !live);

            // Charge going out to pylons: a second, blue beam whose pulses run the other way, toward the pylon.
            if (live && link.ChargeFlow > 0.01f)
            {
                Position chargeFrom = link.ChargeFlowsFromTo ? link.From : link.To;
                Position chargeTo = link.ChargeFlowsFromTo ? link.To : link.From;
                SubmitChargeBeam(beams, link.From, link.To, chargeFrom.X, chargeFrom.Y, chargeTo.X, chargeTo.Y, link.ChargeFlow);
            }
        }
    }

    /// <summary>
    /// A charge beam from cell (<paramref name="x0"/>, <paramref name="y0"/>) to (<paramref name="x1"/>,
    /// <paramref name="y1"/>), its pulses flowing toward the end; <paramref name="a"/> and <paramref name="b"/>
    /// name it across frames.
    /// </summary>
    public static void SubmitChargeBeam(BeamFader beams, Position a, Position b, float x0, float y0, float x1, float y1, float flow) =>
        beams.Submit(new BeamKey(EntityShape.KindChargeBeam, a, b), x0, y0, x1, y1, flow, 3.5f);
}
