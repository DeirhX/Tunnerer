namespace Emberward.Core.Entities.Tanks;

using Emberward.Core.Rendering;
using Emberward.Core.Types;

/// <summary>
/// The tank's one shape: the cells of its sprite bitmap (<see cref="TankSprites.Sprites"/>) that are not transparent,
/// for the direction it faces. Everything that asks where a tank is reads this: bumping into terrain while moving and
/// picking items up (<see cref="TankDigger"/>), projectiles hitting it (<c>CollisionSolver.TestPoint</c>) and crawling
/// machines that may not step onto it (<c>World.IsMachineSpotOccupied</c>), and tanks bumping into another tank or
/// another side's machine (<c>CollisionSolver.BlocksTank</c>).
/// </summary>
/// <remarks>
/// The sprite is the source because it is what the player sees: the Desktop hull and tracks are drawn to cover the
/// same cells. Do not add a second mask or footprint beside it; <c>TankShapeTests</c> checks the uses agree.
/// </remarks>
public static class TankSpriteShape
{
    /// <summary>Width and height of the square the shape fits in, in cells.</summary>
    public const int Size = TankSprites.SpriteWidth;

    /// <summary>How far a shape cell can be from the tank's position along either axis.</summary>
    public const int Radius = Size / 2;

    /// <summary>Whether the tank facing <paramref name="dir"/> at <paramref name="origin"/> covers <paramref name="point"/>.</summary>
    public static bool Contains(int dir, Position origin, Position point)
    {
        int sx = point.X - origin.X + Radius;
        int sy = point.Y - origin.Y + Radius;
        if ((uint)sx >= Size || (uint)sy >= Size)
            return false;
        return Bitmap(dir)[sx + sy * Size] != 0;
    }

    /// <summary>
    /// Whether any cell of the tank facing <paramref name="dir"/> at <paramref name="origin"/> lies in the square of
    /// half-width <paramref name="half"/> centred on <paramref name="center"/>.
    /// </summary>
    public static bool OverlapsSquare(int dir, Position origin, Position center, int half)
    {
        if (Math.Abs(origin.X - center.X) > half + Radius || Math.Abs(origin.Y - center.Y) > half + Radius)
            return false;
        var sprite = Bitmap(dir);
        for (int sy = 0; sy < Size; sy++)
            for (int sx = 0; sx < Size; sx++)
            {
                if (sprite[sx + sy * Size] == 0) continue;
                int x = origin.X - Radius + sx, y = origin.Y - Radius + sy;
                if (Math.Abs(x - center.X) <= half && Math.Abs(y - center.Y) <= half)
                    return true;
            }
        return false;
    }

    /// <summary>
    /// Whether the tank facing <paramref name="dirA"/> at <paramref name="a"/> shares a cell with the tank facing
    /// <paramref name="dirB"/> at <paramref name="b"/>.
    /// </summary>
    public static bool Overlaps(int dirA, Position a, int dirB, Position b)
    {
        if (Math.Abs(a.X - b.X) > 2 * Radius || Math.Abs(a.Y - b.Y) > 2 * Radius)
            return false;
        var sprite = Bitmap(dirA);
        for (int sy = 0; sy < Size; sy++)
            for (int sx = 0; sx < Size; sx++)
            {
                if (sprite[sx + sy * Size] == 0) continue;
                if (Contains(dirB, b, new Position(a.X - Radius + sx, a.Y - Radius + sy)))
                    return true;
            }
        return false;
    }

    /// <summary>
    /// The sprite direction a tank faces after a step by <paramref name="move"/>.
    /// Layout: 0=NE, 1=N, 2=NW, 3=E, 5=W, 6=SE, 7=S, 8=SW (index 4 is unused).
    /// </summary>
    public static int DirectionOf(Offset move)
    {
        int dx = Math.Sign(move.X), dy = Math.Sign(move.Y);
        return (dx, dy) switch
        {
            (1, -1) => 0,
            (0, -1) => 1,
            (-1, -1) => 2,
            (1, 0) => 3,
            (-1, 0) => 5,
            (1, 1) => 6,
            (0, 1) => 7,
            (-1, 1) => 8,
            _ => 1,
        };
    }

    /// <summary>
    /// Iterates all non-transparent pixels of the sprite for the given direction,
    /// calling <paramref name="visitor"/> with (spriteByteValue, worldPosition).
    /// Return false from visitor to break early.
    /// </summary>
    public static void ForEachPixel(int dir, Position origin, Func<byte, Position, bool> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        var sprite = Bitmap(dir);
        for (int sy = 0; sy < Size; sy++)
            for (int sx = 0; sx < Size; sx++)
            {
                byte val = sprite[sx + sy * Size];
                if (val == 0) continue;
                var worldPos = new Position(origin.X - Radius + sx, origin.Y - Radius + sy);
                if (!visitor(val, worldPos)) return;
            }
    }

    private static byte[] Bitmap(int dir) => TankSprites.Sprites[Math.Clamp(dir, 0, TankSprites.DirectionCount - 1)];
}
