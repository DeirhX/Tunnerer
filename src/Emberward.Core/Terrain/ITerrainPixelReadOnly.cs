namespace Emberward.Core.Terrain;

using Emberward.Core.Types;

public interface ITerrainPixelReadOnly
{
    Size Size { get; }
    int Width { get; }
    int Height { get; }
    bool IsInside(Position pos);

    TerrainPixel GetPixel(Position pos);
    TerrainPixel GetPixelRaw(Position pos);
    TerrainPixel GetPixelRaw(int offset);

    ReadOnlySpan<TerrainPixel> Data { get; }
}
