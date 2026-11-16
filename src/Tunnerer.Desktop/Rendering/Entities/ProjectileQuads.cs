namespace Tunnerer.Desktop.Rendering.Entities;

using Tunnerer.Core;
using Tunnerer.Core.Entities.Projectiles;
using Tunnerer.Core.Rendering;
using Tunnerer.Core.Sensing;
using Tunnerer.Core.Types;

/// <summary>Projectiles: glowing shots with a trail.</summary>
public sealed class ProjectileQuads
{
    private ProjectileRenderState[] _projectiles = [];
    private int _projectileCount;

    public void Capture(World world)
    {
        int projectileCount = world.Projectiles.Count;
        if (_projectiles.Length < projectileCount)
            _projectiles = new ProjectileRenderState[Math.Max(projectileCount, 64)];
        _projectileCount = world.Projectiles.CopyRenderStates(_projectiles);
    }

    public void Emit(EntityQuads quads, VisionMap? vision)
    {
        float scale = quads.View.PixelScale;
        float alpha = quads.Alpha;
        for (int i = 0; i < _projectileCount; i++)
        {
            ref readonly ProjectileRenderState projectile = ref _projectiles[i];
            if (!projectile.IsAlive || !Sight.Sees(vision, projectile.Position.X, projectile.Position.Y))
                continue;

            // The snapshot is where the shot is after the latest step; it got there along its velocity.
            float back = 1f - alpha;
            float px = (projectile.Position.X - projectile.Velocity.X * back) * scale - quads.View.CameraPixels.X;
            float py = (projectile.Position.Y - projectile.Velocity.Y * back) * scale - quads.View.CameraPixels.Y;
            if (quads.IsOffScreen(px, py, 6f * scale))
                continue;

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
                        trail, radius, 0f, 0f);
                    break;
                }
                case ProjectileType.Shrapnel:
                {
                    float trail = Math.Clamp(speed * 1.6f, 0.25f, 1.4f);
                    const float radius = 0.22f, glow = 0.8f;
                    quads.Add(px, py, dx, dy, -trail - glow, glow, -glow, glow, body, EntityShape.KindHotShot,
                        trail, radius, 0f, 0f);
                    break;
                }
                default:
                    throw new InvalidOperationException($"No sprite for projectile type {projectile.Type}; add a case.");
            }
        }
    }
}
