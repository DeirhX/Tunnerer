namespace Tunnerer.Core.Resources;

using Tunnerer.Core.Config;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

/// <summary>
/// Puts ore back into the ground (a dead tank's hold, DESIGN.md §6.3): one plain ore cell per unit, in the open or dirt
/// cells nearest the centre, within <see cref="Tweaks.Ore.DeathDropRadius"/>, never under a tank or a machine (ore
/// blocks links, so a cell under a relay would cut it). Ore that finds no room is lost.
/// </summary>
public static class OreDrop
{
    // Offsets within the radius, nearest first; ties broken by row then column so the order is fixed.
    private static readonly Offset[] NearestFirst = BuildOffsets(Tweaks.Ore.DeathDropRadius);

    /// <summary>
    /// Places up to <paramref name="units"/> plain ore cells around <paramref name="center"/>, skipping cells
    /// <paramref name="isOccupied"/> reports as taken (null: none are); returns how many.
    /// </summary>
    public static int Scatter(ITerrainPixelAccessor terrain, Position center, int units,
        Func<Position, bool>? isOccupied = null)
    {
        int placed = 0;
        foreach (var offset in NearestFirst)
        {
            if (placed >= units)
                break;
            var pos = center + offset;
            if (!CanHold(terrain, pos) || isOccupied?.Invoke(pos) == true)
                continue;
            terrain.SetPixel(pos, TerrainPixel.Emberite);
            placed++;
        }
        return placed;
    }

    /// <summary>Open ground (dug or scorched) and dirt take dropped ore; bases, rock, ore and the map border do not.</summary>
    private static bool CanHold(ITerrainPixelAccessor terrain, Position pos)
    {
        if (pos.X < 1 || pos.Y < 1 || pos.X >= terrain.Width - 1 || pos.Y >= terrain.Height - 1)
            return false;
        var pixel = terrain.GetPixelRaw(pos);
        return Pixel.IsOpenGround(pixel) || Pixel.IsSoil(pixel);
    }

    private static Offset[] BuildOffsets(int radius)
    {
        var offsets = new List<Offset>();
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
                if (dx * dx + dy * dy <= radius * radius)
                    offsets.Add(new Offset(dx, dy));
        return offsets
            .OrderBy(o => o.X * o.X + o.Y * o.Y)
            .ThenBy(o => o.Y)
            .ThenBy(o => o.X)
            .ToArray();
    }
}
