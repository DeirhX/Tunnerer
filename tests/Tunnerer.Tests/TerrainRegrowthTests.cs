using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

namespace Tunnerer.Tests;

public class TerrainRegrowthTests
{
    [Fact]
    public void ParallelRegrowth_MatchesSequential()
    {
        var sequential = Grow(deterministicSimulation: true);
        var parallel = Grow(deterministicSimulation: false);

        Assert.True(sequential.Changed > 0, "the scenario must actually regrow something");
        for (int i = 0; i < sequential.Terrain.Size.Area; i++)
        {
            Assert.Equal(sequential.Terrain[i], parallel.Terrain[i]);
            Assert.Equal(sequential.Terrain.GetScorchIntensity(i), parallel.Terrain.GetScorchIntensity(i));
        }
    }

    private static (TerrainGrid Terrain, int Changed) Grow(bool deterministicSimulation)
    {
        // A dirt field with blank holes and scorched patches, so every regrowth rule fires.
        var terrain = new TerrainGrid(new Size(160, 100));
        terrain.Fill(TerrainPixel.Dirt);
        for (int y = 10; y < 90; y += 20)
        {
            for (int x = 10; x < 150; x += 20)
            {
                for (int dy = 0; dy < 8; dy++)
                {
                    for (int dx = 0; dx < 8; dx++)
                    {
                        int offset = (x + dx) + (y + dy) * terrain.Width;
                        bool scorched = (x / 20 + y / 20) % 2 == 0;
                        terrain[offset] = scorched ? TerrainPixel.Decal : TerrainPixel.Blank;
                        if (scorched)
                            terrain.SetScorchIntensity(offset, 200);
                    }
                }
            }
        }
        var before = new TerrainPixel[terrain.Size.Area];
        for (int i = 0; i < before.Length; i++)
            before[i] = terrain[i];

        var regrowth = new TerrainRegrowth(deterministicSimulation, enabled: true, simulationSeed: 77);
        for (int step = 0; step < 600; step++)
            regrowth.Advance(terrain, step);

        int changed = 0;
        for (int i = 0; i < before.Length; i++)
            if (terrain[i] != before[i])
                changed++;
        return (terrain, changed);
    }
}
