namespace Emberward.Desktop.Rendering.Entities;

using Emberward.Core;
using Emberward.Core.Entities.Projectiles;
using Emberward.Core.Rendering;
using Emberward.Core.Sensing;
using Emberward.Core.Types;

/// <summary>Projectiles: glowing shots with a trail.</summary>
public sealed class ProjectileQuads
{
    private ProjectileRenderState[] _projectiles = [];
    private int _projectileCount;
    private bool _held;
    private float _heldAlpha;
    private float _lastAlpha;

    /// <summary>
    /// Freezes the shots where they are drawn now (the world stands still), until the next <see cref="Capture"/>.
    /// </summary>
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
        int projectileCount = world.Projectiles.Count;
        if (_projectiles.Length < projectileCount)
            _projectiles = new ProjectileRenderState[Math.Max(projectileCount, 64)];
        _projectileCount = world.Projectiles.CopyRenderStates(_projectiles);
    }

    public void Emit(EntityQuads quads, VisionMap? vision)
    {
        float scale = quads.View.PixelScale;
        float alpha = _held ? _heldAlpha : quads.Alpha;
        _lastAlpha = quads.Alpha;
        for (int i = 0; i < _projectileCount; i++)
        {
            ref readonly ProjectileRenderState projectile = ref _projectiles[i];
            if (!projectile.IsAlive)
                continue;

            // The snapshot is where the shot is after the latest step; it keeps going along its velocity, so the
            // shader draws it at that place plus the velocity times the step's progress.
            float drawnX = projectile.Position.X + projectile.Velocity.X * alpha;
            float drawnY = projectile.Position.Y + projectile.Velocity.Y * alpha;
            // Sight is tested where the shot is drawn, not where the step left it.
            if (!Sight.Sees(vision, drawnX, drawnY))
                continue;
            if (quads.IsOffScreen(drawnX * scale - quads.View.CameraPixels.X, drawnY * scale - quads.View.CameraPixels.Y, 6f * scale))
                continue;
            float px = _held ? drawnX : projectile.Position.X, py = _held ? drawnY : projectile.Position.Y;
            var motion = _held ? default : new PositionF(projectile.Velocity.X, projectile.Velocity.Y);

            float speed = projectile.Velocity.Length;
            float dx = speed > 1e-4f ? projectile.Velocity.X / speed : 1f;
            float dy = speed > 1e-4f ? projectile.Velocity.Y / speed : 0f;
            Color body = ProjectileList.Behaviors[(int)projectile.Type].DrawColor;

            switch (projectile.Type)
            {
                case ProjectileType.Bullet:
                {
                    float trail = Math.Clamp(speed * 0.9f, 0.8f, 3.5f);
                    const float radius = 0.42f, glow = 1.5f;
                    quads.Add(px, py, dx, dy, -trail - glow, glow, -glow, glow, body, EntityShape.KindHotShot,
                        trail, radius, 0f, 0f, motion);
                    break;
                }
                case ProjectileType.Shrapnel:
                {
                    float trail = Math.Clamp(speed * 1.6f, 0.25f, 1.4f);
                    const float radius = 0.22f, glow = 0.8f;
                    quads.Add(px, py, dx, dy, -trail - glow, glow, -glow, glow, body, EntityShape.KindHotShot,
                        trail, radius, 0f, 0f, motion);
                    break;
                }
                default:
                    throw new InvalidOperationException($"No sprite for projectile type {projectile.Type}; add a case.");
            }
        }
    }
}
