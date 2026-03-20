namespace Tunnerer.Core.Terrain;

using Tunnerer.Core.Types;

/// <summary>
/// Read/write access to terrain pixel data and spatial queries.
/// The narrowest interface for consumers that only need pixel-level terrain interaction.
/// </summary>
public interface ITerrainPixelReader
{
    Size Size { get; }
    int Width { get; }
    int Height { get; }
    bool IsInside(Position pos);

    TerrainPixel GetPixel(Position pos);
    TerrainPixel GetPixelRaw(Position pos);
    TerrainPixel GetPixelRaw(int offset);

    void SetPixel(Position pos, TerrainPixel value);
    void SetPixelRaw(Position pos, TerrainPixel value);
    void SetPixelRaw(int offset, TerrainPixel value);
    void CommitPixel(Position pos);

    ReadOnlySpan<TerrainPixel> Data { get; }
}
