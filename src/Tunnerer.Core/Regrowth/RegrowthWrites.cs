namespace Tunnerer.Core.Regrowth;

using Tunnerer.Core.Terrain;

/// <summary>
/// The pixel writes <see cref="TerrainRegrowth"/> has yet to hear of. Regrowth does not scan the map: it keeps an
/// appointment for each cell that can change, and learns of new ones from the writes listed here, which it reads and
/// empties every step.
/// <para>
/// Only writes that matter to it are listed: a cell that became open, scorched or growing (it needs an appointment),
/// and a cell that became dirt (the open cells beside it now have one more dirt neighbour). Nothing is listed until
/// regrowth has surveyed the map once, so building a level, and a terrain no regrowth runs on, cost nothing here.
/// </para>
/// </summary>
public sealed class RegrowthWrites
{
    private readonly List<int> _cells = new();

    /// <summary>True once regrowth has looked over the whole map and follows it through the writes from then on.</summary>
    public bool Surveyed { get; private set; }

    internal List<int> Cells => _cells;

    /// <summary>A pixel was written: the cell at <paramref name="offset"/> held <paramref name="was"/> and now holds <paramref name="now"/>.</summary>
    public void Note(int offset, TerrainPixel was, TerrainPixel now)
    {
        if (Surveyed && was != now && (TerrainRegrowth.ActsOn(now) || Pixel.IsDirt(now)))
            _cells.Add(offset);
    }

    /// <summary>Pixels were written in bulk without <see cref="Note"/>: regrowth must survey the map again.</summary>
    public void NoteBulkWrite()
    {
        Surveyed = false;
        _cells.Clear();
    }

    internal void MarkSurveyed()
    {
        Surveyed = true;
        _cells.Clear();
    }
}
