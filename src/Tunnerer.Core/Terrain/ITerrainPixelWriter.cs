namespace Tunnerer.Core.Terrain;

using Tunnerer.Core.Types;

public interface ITerrainPixelWriter
{
    void SetPixel(Position pos, TerrainPixel value);
    void SetPixelRaw(Position pos, TerrainPixel value);
    void SetPixelRaw(int offset, TerrainPixel value);
    void CommitPixel(Position pos);
}
