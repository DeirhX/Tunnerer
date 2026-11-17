using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Input;
using Tunnerer.Core.LevelGen;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

namespace Tunnerer.Tests;

public class WorldIntegrationTests
{
    private static readonly Size TestSize = new(320, 200);

    private static (World world, uint[] terrainPixels) SetupWorld()
    {
        var world = TestHelpers.CreateSeededWorld(TestSize);
        var terrainPixels = new uint[TestSize.Area];
        world.Terrain.DrawAllToSurface(terrainPixels);
        return (world, terrainPixels);
    }

    [Fact]
    public void World_AdvanceSingleFrame_DoesNotThrow()
    {
        var (world, _) = SetupWorld();
        world.Advance(_ => TestSimulation.Idle);
    }

    [Fact]
    public void World_Advance100Frames_Runs()
    {
        var (world, _) = SetupWorld();
        TestSimulation.AdvanceIdle(world, frames: 100);

        Assert.Equal(100, world.AdvanceCount);
        Assert.False(world.IsMatchOver);
    }

    [Fact]
    public void Tank_LastLife_DoesNotGoNegative()
    {
        var (world, _) = SetupWorld();
        var tank = world.TankList.Tanks[0];
        tank.LivesLeft = 1;
        tank.Die();
        Assert.Equal(0, tank.LivesLeft);

        TestSimulation.AdvanceIdle(world, frames: 300);
        Assert.Equal(0, tank.LivesLeft);

        TestSimulation.AdvanceIdle(world, frames: 300);
        Assert.Equal(0, tank.LivesLeft);
    }

    [Fact]
    public void World_TanksSpawnAtBases()
    {
        var (world, _) = SetupWorld();
        Assert.True(world.TankList.Tanks.Count >= 2, "Expected at least 2 tanks");

        for (int i = 0; i < world.TankList.Tanks.Count; i++)
        {
            var tank = world.TankList.Tanks[i];
            var baseObj = world.TankBases.GetSpawn(i);
            Assert.NotNull(baseObj);
            Assert.Equal(baseObj.Position, tank.Position);
        }
    }

    [Fact]
    public void TerrainSurface_AfterTankMoves_ShowsTerrainUnderTank()
    {
        var (world, terrainPixels) = SetupWorld();

        world.Advance(i => i == 0
            ? TestSimulation.Move(1, 0)
            : TestSimulation.Idle);
        world.Terrain.DrawChangesToSurface(terrainPixels);

        // The terrain colour buffer holds terrain only: the tank's cell shows the terrain pixel there, never the tank.
        var tank = world.TankList.Tanks[0];
        var baseColor = Pixel.GetColor(world.Terrain.GetPixelRaw(tank.Position)).ToArgb();
        int off = tank.Position.X + tank.Position.Y * TestSize.X;
        Assert.Equal(baseColor, terrainPixels[off]);
    }

    [Fact]
    public void Regrowth_WritesNoLevelGenPixels()
    {
        // Run several regrow passes and verify no LevelGen pixels leak into the materialized terrain
        var (world, _) = SetupWorld();

        // Blank out a region to trigger regrow
        for (int y = 50; y < 60; y++)
            for (int x = 50; x < 60; x++)
                world.Terrain.SetPixel(new Position(x, y), TerrainPixel.Blank);

        // Run many frames to trigger regrow timer
        TestSimulation.AdvanceIdle(world, frames: 500);

        // Verify no LevelGen artifacts
        for (int i = 0; i < TestSize.Area; i++)
        {
            var pix = world.Terrain[i];
            Assert.False(pix == TerrainPixel.LevelGenDirt || pix == TerrainPixel.LevelGenRock || pix == TerrainPixel.LevelGenMark,
                $"LevelGen pixel {pix} found at offset {i} during gameplay");
        }
    }

    [Fact]
    public void Tweaks_SmoothingSteps_MatchesCpp()
    {
        Assert.Equal(-1, Tweaks.LevelGen.SmoothingSteps);
    }

    [Fact]
    public void Tweaks_Constants_MatchCpp()
    {
        Assert.Equal(30, Tweaks.LevelGen.BorderWidth);
        Assert.Equal(300, Tweaks.LevelGen.MaxDirtSpawnOdds);
        Assert.Equal(70, Tweaks.LevelGen.DirtSpawnProgression);
        Assert.Equal(65, Tweaks.LevelGen.DirtTargetPercent);
        Assert.Equal(150, Tweaks.LevelGen.TreeSize);
        Assert.Equal(150, Tweaks.Base.MinDistance);
        Assert.Equal(35, Tweaks.Base.BaseSize);
        Assert.Equal(7, Tweaks.Base.DoorSize);
        Assert.Equal(24, Tweaks.Perf.TargetFps);
        Assert.Equal(64, Tweaks.Perf.SectorSize);
    }
}
