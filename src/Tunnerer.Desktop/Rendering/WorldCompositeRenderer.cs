namespace Tunnerer.Desktop.Rendering;

using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Rendering;
using Tunnerer.Core.Types;

/// <summary>
/// Draws the cell-resolution marker sprites over a copy of the terrain pixels. Everything else that moves
/// (tanks, projectiles, machines, links, the placement preview) is drawn by <see cref="EntityLayerBuilder"/> at screen resolution.
/// </summary>
public sealed class WorldCompositeRenderer
{
    private SpriteRenderState[] _spriteStates = [];

    public void Compose(World world, uint[] terrainPixels, uint[] compositePixels)
    {
        Array.Copy(terrainPixels, compositePixels, terrainPixels.Length);
        var surface = new Surface(compositePixels, world.Terrain.Width, world.Terrain.Height);

        // Links, machines and the placement preview are drawn by the entity layer (EntityLayerBuilder / EntityPs.hlsl).
        int spriteCount = world.Sprites.CopyRenderStates(EnsureCapacity(ref _spriteStates, world.Sprites.Count));
        DrawSprites(surface, _spriteStates, spriteCount);
    }

    private static Span<T> EnsureCapacity<T>(ref T[] buffer, int requiredCount)
    {
        if (buffer.Length < requiredCount)
            buffer = new T[Math.Max(requiredCount, 16)];
        return buffer;
    }

    private static void DrawSprites(Surface surface, SpriteRenderState[] states, int count)
    {
        for (int i = 0; i < count; i++)
        {
            ref readonly var sprite = ref states[i];
            if (!sprite.IsAlive)
                continue;

            uint color = sprite.Type switch
            {
                SpriteType.FailedInteraction => Tweaks.Colors.FailedInteraction.ToArgb(),
                SpriteType.InfoMarker => Tweaks.Colors.InfoMarker.ToArgb(),
                _ => Color.White.ToArgb(),
            };

            int x = sprite.Position.X;
            int y = sprite.Position.Y;
            if (sprite.Type == SpriteType.FailedInteraction)
            {
                SetPixel(surface, x - 1, y - 1, color);
                SetPixel(surface, x + 1, y - 1, color);
                SetPixel(surface, x, y, color);
                SetPixel(surface, x - 1, y + 1, color);
                SetPixel(surface, x + 1, y + 1, color);
            }
            else
            {
                SetPixel(surface, x, y, color);
            }
        }
    }

    private static void SetPixel(Surface surface, int x, int y, uint color)
    {
        if (surface.IsInside(x, y))
            surface.Pixels[x + y * surface.Width] = color;
    }
}
