namespace Tunnerer.Core.Entities.Tanks;

using Tunnerer.Core.Rendering;
using Tunnerer.Core.Types;

/// <summary>
/// The tank's sprite bitmap (<see cref="TankSprites.Sprites"/>) used as a shape: it is what the tank draws, what it
/// bumps into terrain with while moving and what it picks items up with.
/// </summary>
/// <remarks>
/// Tanks have three shapes that are deliberately kept apart, because unifying them would change results: this sprite
/// shape (terrain contact and drawing), <see cref="TankCollisionMask"/> (projectiles hitting a tank, in
/// <c>CollisionSolver</c>) and the square footprint <c>World</c> keeps clear when a machine is planted.
/// </remarks>
internal static class TankSpriteShape
{
    /// <summary>
    /// Iterates all non-transparent pixels of the sprite for the given direction,
    /// calling <paramref name="visitor"/> with (spriteByteValue, worldPosition).
    /// Return false from visitor to break early.
    /// </summary>
    public static void ForEachPixel(int dir, Position origin, Func<byte, Position, bool> visitor)
    {
        dir = Math.Clamp(dir, 0, TankSprites.DirectionCount - 1);
        var sprite = TankSprites.Sprites[dir];
        int w = TankSprites.SpriteWidth, h = TankSprites.SpriteHeight;
        int cx = w / 2, cy = h / 2;

        for (int sy = 0; sy < h; sy++)
            for (int sx = 0; sx < w; sx++)
            {
                byte val = sprite[sx + sy * w];
                if (val == 0) continue;
                var worldPos = new Position(origin.X - cx + sx, origin.Y - cy + sy);
                if (!visitor(val, worldPos)) return;
            }
    }
}
