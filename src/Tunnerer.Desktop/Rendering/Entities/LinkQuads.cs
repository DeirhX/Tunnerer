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
    /// Live and blocked links as beams. A link carrying heat is oriented so its +x runs toward the base, which is
    /// the way the shader's heat pulses flow.
    /// </summary>
    public void Emit(EntityQuads quads)
    {
        float scale = quads.View.PixelScale;
        float phase = quads.Phase;
        for (int i = 0; i < _linkCount; i++)
        {
            ref readonly LinkRenderState link = ref _links[i];
            if (!link.IsAlive || link.Type == LinkType.Theoretical)
                continue;

            Position from = link.FlowsFromTo ? link.From : link.To;
            Position to = link.FlowsFromTo ? link.To : link.From;
            float dx = to.X - from.X, dy = to.Y - from.Y;
            float length = MathF.Sqrt(dx * dx + dy * dy);
            if (length < 0.5f)
                continue;
            float ax = dx / length, ay = dy / length;
            float fx = quads.ScreenX(from.X);
            float fy = quads.ScreenY(from.Y);
            float mx = fx + dx * 0.5f * scale, my = fy + dy * 0.5f * scale;
            if (quads.IsOffScreen(mx, my, (length * 0.5f + 3f) * scale))
                continue;

            float heat = link.Type == LinkType.Live ? link.HeatFlow : 0f;
            float type = link.Type == LinkType.Live ? 0f : 1f;
            // Room for the glow, which the shader grows with the flow.
            float halfWidth = heat > 0.01f ? 4.5f : 1.2f;
            quads.Add(fx, fy, ax, ay, -1.5f, length + 1.5f, -halfWidth, halfWidth,
                EntityShape.KindLink, length, heat, phase, type);

            // Charge going out to pylons: a second, blue beam whose pulses run the other way, toward the pylon.
            if (link.Type == LinkType.Live && link.ChargeFlow > 0.01f)
            {
                Position chargeFrom = link.ChargeFlowsFromTo ? link.From : link.To;
                Position chargeTo = link.ChargeFlowsFromTo ? link.To : link.From;
                AddChargeBeam(quads, chargeFrom.X, chargeFrom.Y, chargeTo.X, chargeTo.Y, link.ChargeFlow);
            }
        }
    }

    /// <summary>A charge beam from cell (<paramref name="x0"/>, <paramref name="y0"/>) to (<paramref name="x1"/>, <paramref name="y1"/>); its pulses flow toward the end.</summary>
    public static void AddChargeBeam(EntityQuads quads, float x0, float y0, float x1, float y1, float flow)
    {
        float scale = quads.View.PixelScale;
        float dx = x1 - x0, dy = y1 - y0;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 0.5f)
            return;
        float fx = quads.ScreenX(x0);
        float fy = quads.ScreenY(y0);
        if (quads.IsOffScreen(fx + dx * 0.5f * scale, fy + dy * 0.5f * scale, (length * 0.5f + 3f) * scale))
            return;
        quads.Add(fx, fy, dx / length, dy / length, -1.5f, length + 1.5f, -3.5f, 3.5f,
            EntityShape.KindChargeBeam, length, flow, quads.Phase, 0f);
    }
}
