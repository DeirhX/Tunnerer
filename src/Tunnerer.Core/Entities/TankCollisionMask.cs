namespace Tunnerer.Core.Entities;

/// <summary>
/// Gameplay collision shape for tanks. Kept separate from render palettes/sprites
/// so physics is not coupled to presentation assets.
/// </summary>
public static class TankCollisionMask
{
    public const int Width = 7;
    public const int Height = 7;
    public const int DirectionCount = 9;

    public static readonly byte[][] Masks =
    [
        [
            0,0,0,1,0,0,0,
            0,0,0,1,1,0,0,
            0,0,1,1,1,1,0,
            1,1,1,1,1,1,1,
            0,1,1,1,1,0,0,
            0,0,1,1,0,0,0,
            0,0,0,1,0,0,0
        ],
        [
            0,0,0,0,0,0,0,
            0,1,0,0,0,1,0,
            0,1,1,1,1,1,0,
            0,1,1,1,1,1,0,
            0,1,1,1,1,1,0,
            0,1,1,1,1,1,0,
            0,1,0,0,0,1,0
        ],
        [
            0,0,0,1,0,0,0,
            0,0,1,1,0,0,0,
            0,1,1,1,1,0,0,
            1,1,1,1,1,1,1,
            0,0,1,1,1,1,0,
            0,0,0,1,1,0,0,
            0,0,0,1,0,0,0
        ],
        [
            0,0,0,0,0,0,0,
            0,1,1,1,1,1,1,
            0,0,1,1,1,1,0,
            0,0,1,1,1,1,0,
            0,0,1,1,1,1,0,
            0,1,1,1,1,1,1,
            0,0,0,0,0,0,0
        ],
        [
            0,0,0,0,0,0,0,
            0,0,0,0,0,0,0,
            0,0,0,0,0,0,0,
            0,0,0,0,0,0,0,
            0,0,0,0,0,0,0,
            0,0,0,0,0,0,0,
            0,0,0,0,0,0,0
        ],
        [
            0,0,0,0,0,0,0,
            1,1,1,1,1,1,0,
            0,1,1,1,1,0,0,
            0,1,1,1,1,0,0,
            0,1,1,1,1,0,0,
            1,1,1,1,1,1,0,
            0,0,0,0,0,0,0
        ],
        [
            0,0,0,1,0,0,0,
            0,0,1,1,0,0,0,
            0,1,1,1,1,0,0,
            1,1,1,1,1,1,1,
            0,0,1,1,1,1,0,
            0,0,0,1,1,0,0,
            0,0,0,1,0,0,0
        ],
        [
            0,1,0,0,0,1,0,
            0,1,1,1,1,1,0,
            0,1,1,1,1,1,0,
            0,1,1,1,1,1,0,
            0,1,1,1,1,1,0,
            0,1,0,0,0,1,0,
            0,0,0,0,0,0,0
        ],
        [
            0,0,0,1,0,0,0,
            0,0,0,1,1,0,0,
            0,0,1,1,1,1,0,
            1,1,1,1,1,1,1,
            0,1,1,1,1,0,0,
            0,0,1,1,0,0,0,
            0,0,0,1,0,0,0
        ],
    ];

    public static bool IsSolidPixel(int direction, int sx, int sy)
    {
        direction = Math.Clamp(direction, 0, DirectionCount - 1);
        if ((uint)sx >= (uint)Width || (uint)sy >= (uint)Height)
            return false;
        return Masks[direction][sx + sy * Width] != 0;
    }
}
