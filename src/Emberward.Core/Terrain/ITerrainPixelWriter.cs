namespace Emberward.Core.Terrain;

using Emberward.Core.Types;

public interface ITerrainPixelWriter
{
    void SetPixel(Position pos, TerrainPixel value);
    void SetPixelRaw(Position pos, TerrainPixel value);
    void SetPixelRaw(int offset, TerrainPixel value);
    void CommitPixel(Position pos);
}
