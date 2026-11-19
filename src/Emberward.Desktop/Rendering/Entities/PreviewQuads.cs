namespace Emberward.Desktop.Rendering.Entities;

using Emberward.Core;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Types;
using Emberward.Core.Network;

/// <summary>
/// What the player's carried machine would do if planted now, drawn from the player tank as its
/// <see cref="MachineSprites"/> says: the ground it would sense, or the ground it would reach (a relay
/// cools, a pylon charges) and the link it would make.
/// </summary>
public sealed class PreviewQuads
{
    private readonly record struct PlacementPreview(bool Active, MachineType Type, LinkPreview Link, float SenseHeat);
    private PlacementPreview _preview;

    /// <summary>The player is the tank in <paramref name="localSlot"/>: while it carries a machine, record where that machine would link or what it would sense.</summary>
    public void Capture(World world, int localSlot)
    {
        _preview = default;
        if (localSlot >= world.TankList.Count)
            return;
        var player = world.TankList.Tanks[localSlot];
        if (player.IsDead || !player.IsCarryingMachine || player.CarriedMachineType is not MachineType type)
            return;
        var preview = world.PreviewPlacement(type, player.Position);
        _preview = new PlacementPreview(true, type, preview.Link, preview.SenseHeat);
    }

    /// <summary>
    /// The carried machine's preview, anchored on the (interpolated) <paramref name="player"/> tank: the reach area,
    /// and a translucent energy stream to the network point it would link to (red past whatever blocks the line).
    /// </summary>
    public void Emit(EntityQuads quads, PositionF player)
    {
        if (!_preview.Active)
            return;
        float phase = quads.Phase;
        float cx = quads.ScreenX(player.X);
        float cy = quads.ScreenY(player.Y);

        MachineSprite sprite = MachineSprites.Get(_preview.Type);
        if (sprite.SenseRadius is float r)
        {
            quads.Add(cx, cy, 1f, 0f, -r - 0.6f, r + 0.6f, -r - 0.6f, r + 0.6f,
                EntityShape.KindSenseRing, _preview.SenseHeat, r, phase, 0f);
        }
        if (sprite.Reach is not MachineReach area)
            return;

        float reach = area.Radius;
        quads.Add(cx, cy, 1f, 0f, -reach - 6f, reach + 6f, -reach - 6f, reach + 6f, area.Color, EntityShape.KindReachArea,
            reach, area.Circle ? 1f : 0f, phase, 0f);

        var link = _preview.Link;
        if (link.Status is LinkPreviewStatus.NoNetwork)
            return;
        float dx = link.Target.X - player.X, dy = link.Target.Y - player.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 0.5f)
            return;
        float status = link.Status switch
        {
            LinkPreviewStatus.Live => 0f,
            LinkPreviewStatus.Blocked => 1f,
            _ => 2f,
        };
        float blockedAt = length;
        if (link.Status == LinkPreviewStatus.Blocked)
        {
            float bx = link.BlockedAt.X - player.X, by = link.BlockedAt.Y - player.Y;
            blockedAt = MathF.Sqrt(bx * bx + by * by);
        }
        // Too far: only a short fading feeler toward the network, not a full line across the map.
        float drawn = link.Status == LinkPreviewStatus.TooFar ? MathF.Min(length, 24f) : length;
        quads.Add(cx, cy, dx / length, dy / length, -1.5f, drawn + 1.5f, -3f, 3f,
            EntityShape.KindPreviewBeam, drawn, status, phase, blockedAt);
    }
}
