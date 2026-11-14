namespace Tunnerer.Core.LevelGen;

using Tunnerer.Core.Config;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

/// <summary>
/// Scatters hot rock pockets over a generated level: embedded in solid ground, away from the bases and from each
/// other, so heat only reaches the players once they dig toward a pocket.
/// </summary>
public static class HotRockPockets
{
    /// <summary>
    /// Places up to <paramref name="count"/> pockets and returns their centres. Deterministic for a given
    /// <paramref name="seed"/>; sites that do not fit the rules after a bounded number of tries are skipped.
    /// </summary>
    public static List<Position> Place(TerrainGrid terrain, IReadOnlyList<Position> bases, int count, int seed)
    {
        var centers = new List<Position>();
        if (count <= 0)
            return centers;

        var rng = new Random(seed);
        int margin = Tweaks.LevelGen.BorderWidth + Tweaks.World.HotRockMaxRadius + 4;
        int minFromBaseSq = Tweaks.World.HotRockMinDistanceFromBase * Tweaks.World.HotRockMinDistanceFromBase;
        int minBetweenSq = Tweaks.World.HotRockMinDistanceBetween * Tweaks.World.HotRockMinDistanceBetween;
        if (terrain.Width <= margin * 2 || terrain.Height <= margin * 2)
            return centers;

        for (int attempt = 0; attempt < 400 && centers.Count < count; attempt++)
        {
            var pos = new Position(
                rng.Next(margin, terrain.Width - margin),
                rng.Next(margin, terrain.Height - margin));
            int radius = rng.Next(Tweaks.World.HotRockMinRadius, Tweaks.World.HotRockMaxRadius + 1);

            if (!IsSolidGround(terrain.GetPixelRaw(pos)))
                continue;
            if (TooClose(pos, bases, minFromBaseSq) || TooClose(pos, centers, minBetweenSq))
                continue;

            if (terrain.PlaceHotRockPocket(pos, radius) > 0)
                centers.Add(pos);
        }

        return centers;
    }

    private static bool IsSolidGround(TerrainPixel pixel) =>
        Pixel.IsDirt(pixel) || pixel == TerrainPixel.DirtGrow || Pixel.IsRock(pixel);

    private static bool TooClose(Position pos, IReadOnlyList<Position> others, int minDistanceSq)
    {
        foreach (var other in others)
            if (Position.DistanceSquared(pos, other) < minDistanceSq)
                return true;
        return false;
    }
}
