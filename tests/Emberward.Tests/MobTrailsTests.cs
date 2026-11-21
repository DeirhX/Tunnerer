using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Entities.Mobs;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Rendering;
using Emberward.Core.Snapshots;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

namespace Emberward.Tests;

/// <summary>
/// The trails emberlings wear in soil (<see cref="MobTrails"/>): crossing a block wears it, a worn block is a lane they
/// run through, the wear fades by formula on read, and none of it touches the terrain.
/// </summary>
public class MobTrailsTests
{
    private static readonly Position Centre = new(60, 100);

    private static (World World, Tank Tank) Arena(TerrainPixel ground)
    {
        var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false, mobs: true);
        for (int y = -30; y <= 30; y++)
            for (int x = -60; x <= 60; x++)
                world.Terrain.SetPixel(Centre + new Offset(x, y), ground);
        return (world, world.TankList.Tanks[0]);
    }

    private static void Tick(World world, Tank tank, int ticks, float heat = 80f)
    {
        for (int i = 0; i < ticks; i++)
        {
            tank.Heat = heat;
            world.Advance(_ => default);
        }
    }

    /// <summary>Speeds only: VectorF's record ToString recurses, so a failed assertion must not print one.</summary>
    private static float[] Speeds(World world)
    {
        var states = new MobRenderState[world.Mobs!.OutCount];
        world.CopyMobRenderStates(states);
        return [.. states.Select(c => c.Velocity.Length)];
    }

    private static int WornBlocks(World world, int now)
    {
        var trails = world.Mobs!.Trails;
        int count = 0;
        for (int y = 0; y < world.Terrain.Height / Tweaks.Mobs.FieldBlockCells; y++)
            for (int x = 0; x < world.Terrain.Width / Tweaks.Mobs.FieldBlockCells; x++)
                if (trails.EffectiveWorn(trails.BlockIndex(x, y), now) > 0f)
                    count++;
        return count;
    }

    private static World DirtHunt(int ticks, int creatures = 6)
    {
        var (world, tank) = Arena(TerrainPixel.Dirt);
        world.Mobs!.AddNest(Centre + new Offset(40, 0), radius: 4, creatures: creatures);
        tank.Position = Centre;
        Tick(world, tank, ticks);
        return world;
    }

    [Fact]
    public void ACreatureCrossingSoil_WearsItsTrail()
    {
        var world = DirtHunt(40);

        Assert.True(world.Mobs!.OutCount > 0);
        Assert.True(WornBlocks(world, world.Mobs.Tick) > 0, "creatures burrowing through dirt should wear blocks");
    }

    [Fact]
    public void ARunThroughOpenGround_WearsNothing()
    {
        var (world, tank) = Arena(TerrainPixel.Blank);
        world.Mobs!.AddNest(Centre + new Offset(40, 0), radius: 1, creatures: 3);
        tank.Position = Centre;
        Tick(world, tank, 20);

        // Only the nest's own ground (radius 1, a few cells) is soil to them, and it is out of the arena's far side.
        Assert.True(WornBlocks(world, world.Mobs.Tick) <= 2);
    }

    [Fact]
    public void AfterSeveralCrossings_ALaneForms_AndIsRunAtOpenSpeed()
    {
        var (world, tank) = Arena(TerrainPixel.Dirt);
        var nest = world.Mobs!.AddNest(Centre + new Offset(40, 0), radius: 4, creatures: 3);
        tank.Position = Centre;
        var trails = world.Mobs.Trails;
        var start = Centre + new Offset(10, 0);
        int block = trails.BlockOf(start);
        for (int i = 0; i < 3; i++)
            Assert.False(trails.IsLane(block, 0), "not a lane before enough crossings");
        // Several crossings by earlier creatures (the same tick).
        int now = world.Mobs.Tick;
        for (int i = 0; i < (int)MathF.Ceiling(Tweaks.Mobs.TrailLaneThreshold / Tweaks.Mobs.TrailWearPerCrossing); i++)
            trails.Wear(block, now);
        Assert.True(trails.IsLane(block, now));

        // Wear every block along the row the creature will take, then watch it run in dirt.
        for (int x = 10; x <= 50; x++)
            for (int k = 0; k < 4; k++)
                trails.Wear(trails.BlockOf(Centre + new Offset(x, 0)), now);
        Assert.True(nest.Out == 0);
        Tick(world, tank, 14);

        Assert.Contains(Speeds(world), v => Math.Abs(v - Tweaks.Mobs.OpenSpeed) < 0.01f);
    }

    [Fact]
    public void WithoutALane_CreaturesInDirtBurrowSlowly()
    {
        // One creature: a crowd wears the same blocks and makes a lane within a few ticks.
        var world = DirtHunt(30, creatures: 1);
        var speeds = Speeds(world);
        Assert.NotEmpty(speeds);
        Assert.All(speeds, v => Assert.True(v <= Tweaks.Mobs.SoilSpeed + 0.01f));
    }

    [Fact]
    public void TheTrailFadesByFormula_AndNeverBelowNothing()
    {
        var (world, _) = Arena(TerrainPixel.Dirt);
        var trails = world.Mobs!.Trails;
        int block = trails.BlockOf(Centre);
        trails.Wear(block, 100);
        trails.Wear(block, 100);
        float worn = 2f * Tweaks.Mobs.TrailWearPerCrossing;

        Assert.Equal(worn, trails.EffectiveWorn(block, 100));
        int life = Tweaks.Mobs.TrailLifetimeTicks;
        Assert.Equal(worn * (1f - 240f / life), trails.EffectiveWorn(block, 340), 4);
        Assert.Equal(0f, trails.EffectiveWorn(block, 100 + life));
        Assert.Equal(0f, trails.EffectiveWorn(block, 100 + 3 * life));

        // Wearing again converts the fading value first, then adds, and caps.
        trails.Wear(block, 100 + life / 2);
        Assert.Equal(worn * 0.5f + Tweaks.Mobs.TrailWearPerCrossing, trails.EffectiveWorn(block, 100 + life / 2), 4);
        for (int i = 0; i < 100; i++)
            trails.Wear(block, 100 + life / 2);
        Assert.Equal(Tweaks.Mobs.TrailWearMax, trails.EffectiveWorn(block, 100 + life / 2));
    }

    [Fact]
    public void CreaturesOut_WriteNothingToTheTerrain()
    {
        var (world, tank) = Arena(TerrainPixel.Dirt);
        world.Mobs!.AddNest(Centre + new Offset(40, 0), radius: 4, creatures: 6);
        tank.Position = Centre;
        var before = Snapshot(world.Terrain);

        Tick(world, tank, 60, heat: 80f);

        Assert.True(world.Mobs.OutCount > 0);
        Assert.True(WornBlocks(world, world.Mobs.Tick) > 0);
        // A bite spills no heat into pixels' material; only temperatures may differ, never the material or cell kind.
        Assert.Equal(before, Snapshot(world.Terrain));
    }

    private static TerrainPixel[] Snapshot(TerrainGrid terrain)
    {
        var copy = new TerrainPixel[terrain.Width * terrain.Height];
        for (int i = 0; i < copy.Length; i++)
            copy[i] = terrain[i];
        return copy;
    }

    [Fact]
    public void TheTrailIsPartOfTheHash_AndOfTheSnapshot()
    {
        var world = DirtHunt(40);
        var trails = world.Mobs!.Trails;
        int block = trails.BlockOf(Centre + new Offset(-50, 25));
        ulong hash = WorldStateHash.Compute(world);
        trails.Wear(block, world.Mobs.Tick);
        Assert.NotEqual(hash, WorldStateHash.Compute(world));

        byte[] saved = WorldSnapshot.Save(world, [null, null]);
        var copy = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false, mobs: true);
        WorldSnapshot.Load(copy, saved);

        Assert.Equal(WorldStateHash.Compute(world), WorldStateHash.Compute(copy));
        Assert.Equal(trails.EffectiveWorn(block, world.Mobs.Tick), copy.Mobs!.Trails.EffectiveWorn(block, world.Mobs.Tick));
        Assert.Equal(saved, WorldSnapshot.Save(copy, [null, null]));
    }

    [Fact]
    public void WithTrailsOn_TwoRunsAgreeOnEveryTick()
    {
        var (a, tankA) = Arena(TerrainPixel.Dirt);
        var (b, tankB) = Arena(TerrainPixel.Dirt);
        foreach (var (w, t) in new[] { (a, tankA), (b, tankB) })
        {
            w.Mobs!.AddNest(Centre + new Offset(40, 0), radius: 4, creatures: 8);
            t.Position = Centre;
        }
        for (int i = 0; i < 120; i++)
        {
            Tick(a, tankA, 1);
            Tick(b, tankB, 1);
            Assert.Equal(WorldStateHash.Compute(a), WorldStateHash.Compute(b));
        }
        Assert.True(WornBlocks(a, a.Mobs!.Tick) > 0);
    }

    [Fact]
    public void WithNothingOut_TheTrailsCostNothing()
    {
        var (world, tank) = Arena(TerrainPixel.Dirt);
        world.Mobs!.AddNest(Centre + new Offset(40, 0), radius: 4, creatures: 6);
        tank.Position = Centre;
        Tick(world, tank, 60, heat: 10f);

        Assert.Equal(0, world.Mobs.OutCount);
        Assert.Equal(0L, world.Mobs.CreatureSteps);
        Assert.Equal(0, WornBlocks(world, world.Mobs.Tick));
    }
}
