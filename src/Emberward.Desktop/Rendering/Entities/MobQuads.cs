namespace Emberward.Desktop.Rendering.Entities;

using Emberward.Core;
using Emberward.Core.Rendering;
using Emberward.Core.Sensing;
using Emberward.Core.Types;

/// <summary>
/// The emberlings: one quad per creature out of its nest (drawn along its velocity, like a shot), and a quad of
/// resting embers over each nest that has creatures in it. Only what the player's sight covers is drawn.
/// </summary>
public sealed class MobQuads
{
    private MobRenderState[] _mobs = [];
    private int _mobCount;
    private NestRenderState[] _nests = [];
    private int _nestCount;
    private bool _held;
    private float _heldAlpha;
    private float _lastAlpha;

    /// <summary>Freezes the creatures where they are drawn now (the world stands still), until the next <see cref="Capture"/>.</summary>
    public void Hold()
    {
        if (_held)
            return;
        _held = true;
        _heldAlpha = _lastAlpha;
    }

    public void Capture(World world)
    {
        _held = false;
        _lastAlpha = 0f;
        int outCount = world.Mobs?.OutCount ?? 0;
        if (_mobs.Length < outCount)
            _mobs = new MobRenderState[Math.Max(outCount, 64)];
        _mobCount = world.CopyMobRenderStates(_mobs);

        int nests = world.Mobs?.Nests.Count ?? 0;
        if (_nests.Length < nests)
            _nests = new NestRenderState[nests];
        _nestCount = world.CopyNestRenderStates(_nests);
    }

    public void Emit(EntityQuads quads, VisionMap? vision)
    {
        float scale = quads.View.PixelScale;
        float alpha = _held ? _heldAlpha : quads.Alpha;
        _lastAlpha = quads.Alpha;

        for (int i = 0; i < _nestCount; i++)
        {
            ref readonly NestRenderState nest = ref _nests[i];
            if (nest.IsCleared || nest.Occupied == 0 || !Sight.Sees(vision, nest.Centre))
                continue;
            float extent = nest.Radius * 0.6f + 2f;
            if (quads.IsOffScreen(quads.ScreenX(nest.Centre.X), quads.ScreenY(nest.Centre.Y), extent * scale))
                continue;
            quads.Add(EntityQuads.Centre(nest.Centre.X), EntityQuads.Centre(nest.Centre.Y), 1f, 0f,
                -extent, extent, -extent, extent, EntityShape.KindMob, 1f, extent * 0.8f, quads.Phase, 1f);
        }

        for (int i = 0; i < _mobCount; i++)
        {
            ref readonly MobRenderState mob = ref _mobs[i];
            float drawnX = mob.Position.X + mob.Velocity.X * alpha;
            float drawnY = mob.Position.Y + mob.Velocity.Y * alpha;
            if (!Sight.Sees(vision, drawnX, drawnY) ||
                quads.IsOffScreen(quads.ScreenX(drawnX), quads.ScreenY(drawnY), 3f * scale))
                continue;

            float speed = mob.Velocity.Length;
            float dx = speed > 1e-4f ? mob.Velocity.X / speed : 1f;
            float dy = speed > 1e-4f ? mob.Velocity.Y / speed : 0f;
            // A held creature is drawn where it was, not moving on; otherwise the shader carries it by its velocity.
            PositionF motion = _held ? default : new PositionF(mob.Velocity.X, mob.Velocity.Y);
            float x = _held ? drawnX : mob.Position.X, y = _held ? drawnY : mob.Position.Y;
            quads.Add(EntityQuads.Centre(x), EntityQuads.Centre(y), dx, dy, -1.7f, 1.3f, -1.3f, 1.3f,
                EntityShape.KindMob, mob.IsCharged ? 1f : 0f, mob.IsBurrowing ? 1f : 0f,
                quads.Phase + mob.Id * 0.37f, 0f, motion);
        }
    }
}
