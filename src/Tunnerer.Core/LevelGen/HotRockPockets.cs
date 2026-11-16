namespace Tunnerer.Core.LevelGen;

using Tunnerer.Core.Config;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

/// <summary>
/// Scatters hot rock pockets over a generated level: embedded in solid ground, away from the bases and from each
/// other, so heat only reaches the players once they dig toward a pocket.
/// The scatter is a gradient: the first pocket is a large placeholder "core" at the map centre, and the rest are
/// biased toward the centre and grow bigger there, so the world gets hotter the deeper (more central) it is dug.
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
        var middle = new Position(terrain.Width / 2, terrain.Height / 2);
        int minFromBaseCore = Tweaks.World.HotRockMinDistanceFromBase;
        // The core converts whatever solid ground lies inside it; open cavities at the centre do not block it.
        if (!TooClose(middle, bases, minFromBaseCore * minFromBaseCore)
            && terrain.PlaceHotRockPocket(middle, Tweaks.World.HotRockCoreRadius) > 0)
            centers.Add(middle);

        int margin = Tweaks.LevelGen.BorderWidth + Tweaks.World.HotRockMaxRadius + 4;
        int minFromBaseSq = Tweaks.World.HotRockMinDistanceFromBase * Tweaks.World.HotRockMinDistanceFromBase;
        int minBetweenSq = Tweaks.World.HotRockMinDistanceBetween * Tweaks.World.HotRockMinDistanceBetween;
        if (terrain.Width <= margin * 2 || terrain.Height <= margin * 2)
            return centers;

        for (int attempt = 0; attempt < 400 && centers.Count < count; attempt++)
        {
            // Distance from the centre as a fraction of the half-extent. Raising a uniform sample to a power > 1
            // pulls the pockets toward the centre; the angle is uniform.
            double fraction = DeterministicMath.Pow(rng.NextDouble(), Tweaks.World.HotRockCentreBias);
            double angle = rng.NextDouble() * Math.PI * 2.0;
            var pos = new Position(
                middle.X + (int)(DeterministicMath.Cos(angle) * fraction * (terrain.Width / 2 - margin)),
                middle.Y + (int)(DeterministicMath.Sin(angle) * fraction * (terrain.Height / 2 - margin)));
            // Pockets grow toward the centre.
            int radius = Tweaks.World.HotRockMinRadius
                + (int)Math.Round((Tweaks.World.HotRockMaxRadius - Tweaks.World.HotRockMinRadius) * (1.0 - fraction));

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
