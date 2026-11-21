namespace Emberward.Desktop.Rendering.Entities;

using Emberward.Core;
using Emberward.Core.Rendering;
using Emberward.Core.Sensing;
using Emberward.Core.Types;

/// <summary>
/// The emberites: one quad per creature out of its nest (drawn along its velocity, like a shot), and a quad of
/// resting embers over each nest that has creatures in it, flaring while the nest stirs. Only what the player's sight covers is drawn.
/// </summary>
public sealed class EmberiteQuads
{
    private EmberiteRenderState[] _emberites = [];
    private int _emberiteCount;
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
        int outCount = world.Emberites?.OutCount ?? 0;
        if (_emberites.Length < outCount)
            _emberites = new EmberiteRenderState[Math.Max(outCount, 64)];
        _emberiteCount = world.CopyEmberiteRenderStates(_emberites);

        int nests = world.Emberites?.Nests.Count ?? 0;
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
            // 2 while it stirs: the shader flares the embers as the warning of a wave.
            quads.Add(EntityQuads.Centre(nest.Centre.X), EntityQuads.Centre(nest.Centre.Y), 1f, 0f,
                -extent, extent, -extent, extent, EntityShape.KindEmberite, nest.IsStirring ? 2f : 1f, extent * 0.8f, quads.Phase, 1f);
        }

        for (int i = 0; i < _emberiteCount; i++)
        {
            ref readonly EmberiteRenderState emberite = ref _emberites[i];
            float drawnX = emberite.Position.X + emberite.Velocity.X * alpha;
            float drawnY = emberite.Position.Y + emberite.Velocity.Y * alpha;
            if (!Sight.Sees(vision, drawnX, drawnY) ||
                quads.IsOffScreen(quads.ScreenX(drawnX), quads.ScreenY(drawnY), 3f * scale))
                continue;

            float speed = emberite.Velocity.Length;
            float dx = speed > 1e-4f ? emberite.Velocity.X / speed : 1f;
            float dy = speed > 1e-4f ? emberite.Velocity.Y / speed : 0f;
            // A held creature is drawn where it was, not moving on; otherwise the shader carries it by its velocity.
            PositionF motion = _held ? default : new PositionF(emberite.Velocity.X, emberite.Velocity.Y);
            float x = _held ? drawnX : emberite.Position.X, y = _held ? drawnY : emberite.Position.Y;
            quads.Add(EntityQuads.Centre(x), EntityQuads.Centre(y), dx, dy, -1.7f, 1.3f, -1.3f, 1.3f,
                EntityShape.KindEmberite, emberite.IsCharged ? 1f : 0f, emberite.IsBurrowing ? 1f : 0f,
                quads.Phase + emberite.Id * 0.37f, 0f, motion);
        }
    }
}
