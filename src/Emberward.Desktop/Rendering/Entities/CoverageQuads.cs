namespace Emberward.Desktop.Rendering.Entities;

using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Types;
using Emberward.Core.Network;

/// <summary>
/// The player's network coverage (DESIGN.md §4.6): a faint field around every node, and while the tank is covered
/// (outside a base) a cool tether from the node carrying its heat away, and while its hold unloads into the network a
/// stream of ore crystals beside it. Off network the fields brighten a little, so the way back reads at a glance.
/// </summary>
public sealed class CoverageQuads
{
    private readonly List<CoverageNode> _coverageNodes = new();
    private TankCoverage _playerCoverage;
    private bool _playerOffNetwork;
    private bool _playerUnloading;

    public void Capture(World world, int localSlot)
    {
        _coverageNodes.Clear();
        _playerCoverage = TankCoverage.None;
        _playerOffNetwork = false;
        _playerUnloading = false;
        if (localSlot >= world.TankList.Count || world.TankList.Tanks[localSlot] is not { Base: { } home } player)
            return;
        world.CollectCoverageNodes(home, _coverageNodes);
        _playerCoverage = player.IsDead ? TankCoverage.None : player.Coverage;
        _playerOffNetwork = !player.IsDead && player.IsOffNetwork;
        _playerUnloading = player.IsUnloadingOre && _playerCoverage.Covered;
    }

    /// <summary>The coverage fields around every node.</summary>
    public void Emit(EntityQuads quads)
    {
        float scale = quads.View.PixelScale;
        float phase = quads.Phase;
        float emphasis = _playerOffNetwork ? 1f : 0f;
        foreach (var node in _coverageNodes)
        {
            float r = node.Radius;
            if (quads.IsOffScreen(quads.ScreenX(node.Position.X), quads.ScreenY(node.Position.Y), (r + 4f) * scale))
                continue;
            float nx = EntityQuads.Centre(node.Position.X);
            float ny = EntityQuads.Centre(node.Position.Y);
            quads.Add(nx, ny, 1f, 0f, -r - 4f, r + 4f, -r - 4f, r + 4f, Tweaks.Colors.Relay, EntityShape.KindCoverage,
                r, node.Strength, phase, emphasis);
        }

    }

    /// <summary>
    /// The tether (and the ore stream while the hold unloads), handed to <paramref name="beams"/>;
    /// <paramref name="player"/> is the interpolated player tank, where they start.
    /// </summary>
    public void SubmitTether(BeamFader beams, PositionF player)
    {
        if (!_playerCoverage.Covered)
            return;
        float dx = player.X - _playerCoverage.Node.X, dy = player.Y - _playerCoverage.Node.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 4f)
            return;   // standing on the node (or in the base): nothing to draw
        // From the tank toward the node: heat flows that way. Keyed by the node, so it follows the tank about.
        Position node = _playerCoverage.Node;
        beams.Submit(new BeamKey(EntityShape.KindTether, node, node),
            player.X, player.Y, node.X, node.Y, _playerCoverage.Strength, 2.5f);
        if (_playerUnloading)
            beams.Submit(new BeamKey(EntityShape.KindOreBeam, node, node), player.X, player.Y, node.X, node.Y, 1f, 1.5f);
    }
}
