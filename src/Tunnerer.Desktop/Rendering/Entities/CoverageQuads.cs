namespace Tunnerer.Desktop.Rendering.Entities;

using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Links;
using Tunnerer.Core.Types;

/// <summary>
/// The player's network coverage (DESIGN.md §4.6): a faint field around every node, and while the tank is covered
/// (outside a base) a cool tether from the node carrying its heat away. Off network the fields brighten a little, so
/// the way back reads at a glance.
/// </summary>
public sealed class CoverageQuads
{
    private readonly List<CoverageNode> _coverageNodes = new();
    private TankCoverage _playerCoverage;
    private bool _playerOffNetwork;

    public void Capture(World world)
    {
        _coverageNodes.Clear();
        _playerCoverage = TankCoverage.None;
        _playerOffNetwork = false;
        if (world.TankList.Count == 0 || world.TankList.Tanks[0] is not { Base: { } home } player)
            return;
        world.CollectCoverageNodes(home, _coverageNodes);
        _playerCoverage = player.IsDead ? TankCoverage.None : player.Coverage;
        _playerOffNetwork = !player.IsDead && player.IsOffNetwork;
    }

    /// <summary><paramref name="player"/> is the interpolated player tank, where the tether starts.</summary>
    public void Emit(EntityQuads quads, PositionF player)
    {
        float scale = quads.View.PixelScale;
        float phase = quads.Phase;
        float emphasis = _playerOffNetwork ? 1f : 0f;
        foreach (var node in _coverageNodes)
        {
            float nx = quads.ScreenX(node.Position.X);
            float ny = quads.ScreenY(node.Position.Y);
            float r = node.Radius;
            if (quads.IsOffScreen(nx, ny, (r + 4f) * scale))
                continue;
            quads.Add(nx, ny, 1f, 0f, -r - 4f, r + 4f, -r - 4f, r + 4f, Tweaks.Colors.Relay, EntityShape.KindCoverage,
                r, node.Strength, phase, emphasis);
        }

        if (!_playerCoverage.Covered)
            return;
        float dx = player.X - _playerCoverage.Node.X, dy = player.Y - _playerCoverage.Node.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 4f)
            return;   // standing on the node (or in the base): nothing to draw
        // Oriented from the tank toward the node: heat flows that way.
        float tx = quads.ScreenX(player.X);
        float ty = quads.ScreenY(player.Y);
        quads.Add(tx, ty, -dx / length, -dy / length, -1.5f, length + 1.5f, -2.5f, 2.5f,
            EntityShape.KindTether, length, _playerCoverage.Strength, phase, 0f);
    }
}
