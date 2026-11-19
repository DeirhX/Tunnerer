using Emberward.Core;
using Emberward.Core.Bots;
using Emberward.Core.Config;
using Emberward.Core.Entities;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Input;
using Emberward.Core.Match;
using Emberward.Core.Entities.Projectiles;
using Emberward.Core.Snapshots;
using Emberward.Core.Terrain;
using Emberward.Core.Types;
using static Emberward.Tests.SnapshotRoundTrip;

namespace Emberward.Tests;

/// <summary>
/// Round trips (<see cref="SnapshotRoundTrip.AssertPlaysOnAlike"/>) of tanks, bots and the goal: bots mid-manoeuvre, a
/// tank respawning and then shielded, build and upgrade keys held across the load, a core tap mid-hold.
/// </summary>
public class SnapshotTankRoundTripTests
{
    [Fact]
    public void BotsMidMatch_PlayOnAlike()
    {
        var original = BotWorld();
        BotTankAI?[] bots = [new BotTankAI(seed: 11), new BotTankAI(seed: 22)];
        Run(original, bots, 0, 1200, (_, _) => default);

        AssertPlaysOnAlike(original, bots, BotWorld, ticks: 600, (_, _) => default);
    }

    [Fact]
    public void TankRespawningAndThenProtected_PlaysOnAlike()
    {
        var world = TestHelpers.CreateSeededWorld();
        var tank = world.TankList.Tanks[1];
        BotTankAI?[] noBots = [null, null];
        Run(world, noBots, 0, 20, (_, _) => default);
        tank.TakeDamage(tank.Reactor.Health + 10, DamageCause.Overheat);
        Run(world, noBots, 20, 5, (_, _) => default);
        Assert.True(tank.IsDead, "the tank must be waiting to respawn when the snapshot is taken");

        Script leaveBase = (frame, i) => i == 1 && frame > 120 ? TestSimulation.Move(0, 1) : default;
        var loaded = AssertPlaysOnAlike(world, noBots, () => TestHelpers.CreateSeededWorld(), ticks: 400, leaveBase,
            firstFrame: 25);
        Assert.False(loaded.TankList.Tanks[1].IsDead);
    }

    [Fact]
    public void ShieldUpAndBuildKeyHeldAcrossTheLoad_PlayOnAlike()
    {
        var world = TestHelpers.CreateSeededWorld();
        var tank = world.TankList.Tanks[0];
        tank.Spawn(world.Terrain.Ledger);
        Script holdBuild = (frame, i) => i != 0 ? default : new ControllerOutput
        {
            MoveSpeed = new Offset(0, frame is > 10 and < 90 ? 1 : 0),
            BuildPrimary = frame is > 5 and < 60,
            Upgrade = frame is > 20 and < 40 ? 1 : 0,
        };
        BotTankAI?[] noBots = [null, null];
        Run(world, noBots, 0, 30, holdBuild);
        Assert.True(tank.SpawnProtectionTicks > 0, "the shield must be up when the snapshot is taken");

        AssertPlaysOnAlike(world, noBots, () => TestHelpers.CreateSeededWorld(), ticks: 200, holdBuild, firstFrame: 30);
    }

    [Fact]
    public void CoreTapMidHold_PlaysOnAlike()
    {
        // The hold time decays once the tap stops meeting the goal, so it is set just before the save: set at the
        // start, it was back to 0 by then, as in a built world, and leaving it out of the snapshot passed.
        var world = WorldWithCore();
        BotTankAI?[] bots = [new BotTankAI(seed: 5), new BotTankAI(seed: 6)];
        Run(world, bots, 0, 240, (_, _) => default);
        typeof(CoreTap).GetProperty(nameof(CoreTap.HoldSeconds))!
            .SetValue(world.CoreTapOf(world.TankList.Tanks[0].Base), 1.5);

        AssertPlaysOnAlike(world, bots, WorldWithCore, ticks: 240, (_, _) => default);
    }

    private static World WorldWithCore()
    {
        var size = new Size(480, 300);
        var (terrain, spawns) = new Core.LevelGen.ToastGenerator().Generate(size, seed: 42, playerCount: 2);
        var world = new World(size, simulationSeed: 42, new MatchRules { HotRockPockets = 3, CoreTapWins = true });
        world.Initialize(terrain, spawns, decorationSeed: 43);
        return world;
    }
}
