namespace Tunnerer.Core.Terrain;

public partial class TerrainGrid
{
    public void MaterializeTerrain(bool parallel = false)
    {
        if (parallel)
        {
            const int chunkSize = 4096;
            int length = _data.Length;
            int chunks = (length + chunkSize - 1) / chunkSize;

            Parallel.For(0, chunks, chunk =>
            {
                int from = chunk * chunkSize;
                int to = Math.Min(from + chunkSize, length);
                for (int i = from; i < to; i++)
                    _data[i] = MaterializePixel(_data[i]);
            });
            return;
        }

        for (int i = 0; i < _data.Length; i++)
            _data[i] = MaterializePixel(_data[i]);
    }

    private static TerrainPixel MaterializePixel(TerrainPixel p) => p switch
    {
        TerrainPixel.LevelGenDirt => TerrainPixel.Dirt,
        TerrainPixel.LevelGenRock or TerrainPixel.LevelGenMark => TerrainPixel.Rock,
        _ => p,
    };
}
