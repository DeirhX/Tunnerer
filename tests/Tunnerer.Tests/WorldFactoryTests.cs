using Tunnerer.Core.LevelGen;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;
using Tunnerer.Desktop;

namespace Tunnerer.Tests;

[Collection(ConsoleTestGroup.Name)]
public class WorldFactoryTests
{
    private static readonly Size SmallSize = new(320, 200);

    [Fact]
    public void TerrainTest_CopiesThePatternsAndHasNoTanks()
    {
        var size = new Size(96, 64);
        var built = WorldFactory.Build(new WorldLaunchOptions(
            TerrainTest: true, TerrainSizeOverride: size, LevelGenMode.Deterministic, Seed: 7));

        Assert.Equal(size, built.TerrainSize);
        Assert.Empty(built.World.TankList.Tanks);
        var expected = TerrainTestPatternGenerator.Generate(size);
        for (int i = 0; i < size.Area; i++)
            Assert.Equal(expected[i], built.World.Terrain[i]);
    }

    [Fact]
    public void Match_SameSeedBuildsTheSameTerrain()
    {
        var options = new WorldLaunchOptions(
            TerrainTest: false, TerrainSizeOverride: SmallSize, LevelGenMode.Deterministic, Seed: 123);
        var a = WorldFactory.Build(options);
        var b = WorldFactory.Build(options);

        Assert.Equal(SmallSize, a.TerrainSize);
        Assert.NotEmpty(a.World.TankList.Tanks);
        Assert.False(a.Parallel);
        Assert.Equal(125, a.BotSeed);
        for (int i = 0; i < SmallSize.Area; i++)
            Assert.Equal(a.World.Terrain[i], b.World.Terrain[i]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FastGeneration_IsParallelAndUsesTheGivenSeed(bool terrainTest)
    {
        // --fast is not "deterministic" (an open decision, see WorldFactory), but it builds from the seed it is given
        // so the pause menu's replay arguments rebuild the same map.
        Assert.False(WorldFactory.IsSimulationDeterministic(LevelGenMode.Optimized));
        Assert.True(WorldFactory.IsSimulationDeterministic(LevelGenMode.Deterministic));

        var built = WorldFactory.Build(new WorldLaunchOptions(
            terrainTest, SmallSize, LevelGenMode.Optimized, Seed: 123));
        Assert.True(built.Parallel);
        Assert.Equal(123, built.Seed);
        Assert.Equal(built.Seed + 2, built.BotSeed);
    }
}
