namespace Emberward.Core.LevelGen;

using Emberward.Core.Config;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

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
    public static List<Position> Place(TerrainGrid terrain, IReadOnlyList<Position> bases, int count, int seed) =>
        Place(terrain, bases, count, seed, out _);

    /// <summary>
    /// As <see cref="Place(TerrainGrid, IReadOnlyList{Position}, int, int)"/>, also returning the core (the sector
    /// goal, DESIGN.md §4.8): at the map centre, or the nearest spot to it that is far enough from every base.
    /// </summary>
    public static List<Position> Place(TerrainGrid terrain, IReadOnlyList<Position> bases, int count, int seed,
        out Position? core)
    {
        var centers = new List<Position>();
        core = null;
        if (count <= 0)
            return centers;

        var rng = new Random(seed);
        var middle = new Position(terrain.Width / 2, terrain.Height / 2);
        int minFromBaseCore = Tweaks.World.HotRockMinDistanceFromBase;
        // The core converts whatever solid ground lies inside it; open cavities at the centre do not block it. A base
        // too close to the centre pushes it outward, to the nearest ring position clear of every base.
        if (FindCoreSite(terrain, bases, middle, minFromBaseCore * minFromBaseCore) is Position site
            && terrain.PlaceHotRockPocket(site, Tweaks.World.HotRockCoreRadius) > 0)
        {
            centers.Add(site);
            core = site;
        }

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

            if (!Pixel.IsSolidGround(terrain.GetPixelRaw(pos)))
                continue;
            if (GeneratorUtils.TooClose(pos, bases, minFromBaseSq) || GeneratorUtils.TooClose(pos, centers, minBetweenSq))
                continue;

            if (terrain.PlaceHotRockPocket(pos, radius) > 0)
                centers.Add(pos);
        }

        return centers;
    }

    private static Position? FindCoreSite(TerrainGrid terrain, IReadOnlyList<Position> bases, Position middle, int minFromBaseSq)
    {
        if (!GeneratorUtils.TooClose(middle, bases, minFromBaseSq))
            return middle;
        int margin = Tweaks.LevelGen.BorderWidth + Tweaks.World.HotRockCoreRadius + 2;
        int maxRadius = Math.Min(terrain.Width, terrain.Height) / 2;
        for (int r = 4; r <= maxRadius; r += 4)
            for (int k = 0; k < 24; k++)
            {
                double angle = k * Math.PI * 2.0 / 24.0;
                var p = new Position(middle.X + (int)Math.Round(DeterministicMath.Cos(angle) * r), middle.Y + (int)Math.Round(DeterministicMath.Sin(angle) * r));
                if (p.X < margin || p.Y < margin || p.X >= terrain.Width - margin || p.Y >= terrain.Height - margin)
                    continue;
                if (!GeneratorUtils.TooClose(p, bases, minFromBaseSq))
                    return p;
            }
        return null;
    }

}
