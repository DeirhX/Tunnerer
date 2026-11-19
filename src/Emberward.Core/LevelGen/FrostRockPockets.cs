namespace Emberward.Core.LevelGen;

using Emberward.Core.Config;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

/// <summary>
/// Scatters frost pockets over a generated level (DESIGN.md §4.7): embedded in solid ground at middle distances from
/// the bases, away from hot pockets and from each other. They are places to cool down on a dive, and a hazard if the
/// tank stays too long.
/// </summary>
public static class FrostRockPockets
{
    /// <summary>
    /// Places up to <paramref name="count"/> pockets and returns their centres. Deterministic for a given
    /// <paramref name="seed"/>; sites that do not fit the rules after a bounded number of tries are skipped.
    /// </summary>
    public static List<Position> Place(TerrainGrid terrain, IReadOnlyList<Position> bases,
        IReadOnlyList<Position> hotPockets, int count, int seed)
    {
        var centers = new List<Position>();
        if (count <= 0)
            return centers;

        var rng = new Random(seed);
        int margin = Tweaks.LevelGen.BorderWidth + Tweaks.World.FrostRockMaxRadius + 4;
        if (terrain.Width <= margin * 2 || terrain.Height <= margin * 2)
            return centers;
        int minFromBaseSq = Tweaks.World.FrostRockMinDistanceFromBase * Tweaks.World.FrostRockMinDistanceFromBase;
        int minFromHotSq = Tweaks.World.FrostRockMinDistanceFromHot * Tweaks.World.FrostRockMinDistanceFromHot;
        int minBetweenSq = Tweaks.World.FrostRockMinDistanceBetween * Tweaks.World.FrostRockMinDistanceBetween;

        for (int attempt = 0; attempt < 400 && centers.Count < count; attempt++)
        {
            var pos = new Position(
                margin + rng.Next(terrain.Width - margin * 2),
                margin + rng.Next(terrain.Height - margin * 2));
            int radius = rng.Next(Tweaks.World.FrostRockMinRadius, Tweaks.World.FrostRockMaxRadius + 1);

            var pixel = terrain.GetPixelRaw(pos);
            if (!Pixel.IsSolidGround(pixel))
                continue;
            if (GeneratorUtils.TooClose(pos, bases, minFromBaseSq) || GeneratorUtils.TooClose(pos, hotPockets, minFromHotSq) ||
                GeneratorUtils.TooClose(pos, centers, minBetweenSq))
                continue;

            if (terrain.PlaceFrostRockPocket(pos, radius) > 0)
                centers.Add(pos);
        }

        return centers;
    }
}
