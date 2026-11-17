namespace Tunnerer.Core.Terrain;

/// <summary>
/// The cells that started or stopped blocking a line of sight (<see cref="Pixel.BlocksLink"/>) since the link map
/// last asked. The link map keeps what it knows about each link and looks again only at the links that pass near
/// these cells, so it needs to hear of every such write; every pixel write reports here.
/// <para>
/// Nothing is listed until a link map follows the terrain (<see cref="Follow"/>), so a terrain without one keeps no
/// list. There is one list, for one link map: taking the cells empties it.
/// </para>
/// </summary>
public sealed class SightlineChanges
{
    private readonly List<int> _cells = new();

    /// <summary>True once a link map follows this terrain.</summary>
    public bool IsFollowed { get; private set; }

    /// <summary>True when pixels were written in bulk: any line of sight may have changed.</summary>
    public bool AllChanged { get; private set; }

    /// <summary>The cells listed since <see cref="Clear"/>, as offsets, in the order they were written.</summary>
    internal List<int> Cells => _cells;

    /// <summary>A pixel was written: the cell at <paramref name="offset"/> held <paramref name="was"/> and now holds <paramref name="now"/>.</summary>
    public void Note(int offset, TerrainPixel was, TerrainPixel now)
    {
        if (IsFollowed && Pixel.BlocksLink(was) != Pixel.BlocksLink(now))
            _cells.Add(offset);
    }

    /// <summary>Pixels were written in bulk without <see cref="Note"/>.</summary>
    public void NoteBulkWrite()
    {
        if (!IsFollowed)
            return;
        AllChanged = true;
        _cells.Clear();
    }

    /// <summary>Starts listing. What was written before is not listed: the follower looks at everything once.</summary>
    internal void Follow() => IsFollowed = true;

    internal void Clear()
    {
        AllChanged = false;
        _cells.Clear();
    }
}
