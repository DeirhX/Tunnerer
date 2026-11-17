namespace Tunnerer.Core.Terrain;

/// <summary>
/// Which tiles of the map may hold a cell that regrowth acts on (open ground, scorch, dirt growing back). A map is
/// mostly solid ground that regrowth has nothing to do with, so <see cref="TerrainRegrowth"/> visits only marked
/// tiles and its cost follows what has been dug, not the size of the map.
/// <para>
/// A mark is a "maybe": every tile starts marked, a pixel write that puts such a cell in a tile marks it, and a
/// regrowth pass that finds none in a marked tile clears it. A cleared tile therefore never holds one, and skipping
/// it gives exactly the result of scanning it.
/// </para>
/// </summary>
public sealed class RegrowthTiles
{
    /// <summary>Cells along a tile's side.</summary>
    public const int TileSize = Config.Tweaks.World.RegrowthTileSize;

    private readonly bool[] _marked;
    private readonly bool[] _found;
    private readonly int _width;

    public RegrowthTiles(int width, int height)
    {
        _width = width;
        CountX = (width + TileSize - 1) / TileSize;
        CountY = (height + TileSize - 1) / TileSize;
        _marked = new bool[CountX * CountY];
        _found = new bool[CountX * CountY];
        MarkAll();
    }

    public int CountX { get; }
    public int CountY { get; }

    /// <summary>True for a pixel regrowth does something with: it grows dirt in, fades, or finishes growing.</summary>
    public static bool ActsOn(TerrainPixel pixel) =>
        pixel == TerrainPixel.Blank || pixel == TerrainPixel.DirtGrow || Pixel.IsScorched(pixel);

    public bool IsMarked(int tileX, int tileY) => _marked[tileX + tileY * CountX];

    /// <summary>Marks the tile of a cell if <paramref name="written"/> is a pixel regrowth acts on.</summary>
    public void NoteWrite(int offset, TerrainPixel written)
    {
        if (ActsOn(written))
            _marked[offset % _width / TileSize + offset / _width / TileSize * CountX] = true;
    }

    /// <summary>Marks every tile, after a bulk write that did not go cell by cell through <see cref="NoteWrite"/>.</summary>
    public void MarkAll() => Array.Fill(_marked, true);

    /// <summary>
    /// Marks exactly the tiles that hold a cell regrowth acts on, from the finished map. A world does this once it
    /// is built, so that the first regrowth pass does not have to scan every tile to find out.
    /// </summary>
    public void Rebuild(ReadOnlySpan<TerrainPixel> pixels)
    {
        Array.Clear(_marked);
        int height = pixels.Length / _width;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<TerrainPixel> row = pixels.Slice(y * _width, _width);
            int tileRow = y / TileSize * CountX;
            for (int x = 0; x < row.Length; x++)
                if (ActsOn(row[x]))
                    _marked[tileRow + x / TileSize] = true;
        }
    }

    /// <summary>Starts a regrowth pass: nothing has been found yet.</summary>
    public void BeginPass() => Array.Clear(_found);

    /// <summary>A regrowth pass saw a cell it acts on in this tile. Safe to call from several threads.</summary>
    public void NoteFound(int tileX, int tileY) => _found[tileX + tileY * CountX] = true;

    /// <summary>Ends a regrowth pass: a marked tile in which the pass found nothing is cleared.</summary>
    public void EndPass()
    {
        for (int i = 0; i < _marked.Length; i++)
            _marked[i] = _marked[i] && _found[i];
    }
}
