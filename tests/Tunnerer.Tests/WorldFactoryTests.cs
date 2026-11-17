using Tunnerer.Core.LevelGen;
using Tunnerer.Core.Types;
using Tunnerer.Desktop;
using Tunnerer.Core.Entities.Tanks;

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
        Assert.Equal(125, a.BotSeedFor(slot: 1));
        for (int i = 0; i < SmallSize.Area; i++)
            Assert.Equal(a.World.Terrain[i], b.World.Terrain[i]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FastGeneration_IsParallelAndUsesTheGivenSeed(bool terrainTest)
    {
        // --fast builds from the seed it is given, so the pause menu's replay arguments rebuild the same map.
        var built = WorldFactory.Build(new WorldLaunchOptions(
            terrainTest, SmallSize, LevelGenMode.Optimized, Seed: 123));
        Assert.True(built.Parallel);
        Assert.Equal(123, built.Seed);
        Assert.Equal(built.Seed + 2, built.BotSeedFor(slot: 1));
    }

    [Fact]
    public void FastMatch_SameSeedBuildsTheSameTerrainAndLinkIds()
    {
        var options = new WorldLaunchOptions(
            TerrainTest: false, TerrainSizeOverride: SmallSize, LevelGenMode.Optimized, Seed: 123);
        var a = WorldFactory.Build(options);
        var b = WorldFactory.Build(options);

        for (int i = 0; i < SmallSize.Area; i++)
            Assert.Equal(a.World.Terrain[i], b.World.Terrain[i]);
        Assert.NotEmpty(a.World.LinkMap.Points);
        Assert.Equal(a.World.LinkMap.Points.Select(p => p.Id), b.World.LinkMap.Points.Select(p => p.Id));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(8)]
    public void MapForMorePlayers_HasThatManyBasesWellApart(int players)
    {
        var size = new Size(480, 300);
        // No two bases overlap or touch: each tank starts with ground of its own around it.
        int minSq = Core.Config.Tweaks.Base.BaseSize * Core.Config.Tweaks.Base.BaseSize * 4;
        for (int seed = 1; seed <= 5; seed++)
        {
            var (_, spawns) = new ToastGenerator().Generate(size, seed, LevelGenMode.Optimized, players);

            Assert.Equal(players, spawns.Length);
            for (int i = 0; i < spawns.Length; i++)
                for (int j = i + 1; j < spawns.Length; j++)
                    Assert.True(Position.DistanceSquared(spawns[i], spawns[j]) >= minSq,
                        $"seed {seed}: bases {i} and {j} are too close");
        }
    }

    [Fact]
    public void NetworkMatchOptions_GiveATankPerPlayerAndARaceToTheCore()
    {
        var built = WorldFactory.Build(new WorldLaunchOptions(
            TerrainTest: false, SmallSize, LevelGenMode.Optimized, Seed: 5, Players: 4, CoreTapWins: true));

        Assert.Equal(4, built.World.TankList.Count);
        Assert.True(built.World.Rules.CoreTapWins);
    }

    [Fact]
    public void TwoPlayerMaps_AreUnchangedByThePlayerCountOption()
    {
        var gen = new ToastGenerator();
        var (_, byDefault) = gen.Generate(SmallSize, seed: 123);
        var (_, explicitTwo) = gen.Generate(SmallSize, seed: 123, playerCount: 2);

        Assert.Equal(byDefault, explicitTwo);
    }
}
