using Tunnerer.Core;
using Tunnerer.Core.Bots;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Entities.Tanks;
using Tunnerer.Core.Input;
using Tunnerer.Core.Match;
using Tunnerer.Core.Entities.Projectiles;
using Tunnerer.Core.Snapshots;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;
using static Tunnerer.Tests.SnapshotRoundTrip;

namespace Tunnerer.Tests;

/// <summary>
/// Round trips (<see cref="SnapshotRoundTrip.AssertPlaysOnAlike"/>) whose state shows only after the load: machines
/// wrecked, a link cut, a machine carried, regrowth and scorch busy, a shot fired on the save tick.
/// </summary>
public class SnapshotMachineRoundTripTests
{
    [Fact]
    public void LinkCutAfterTheLoad_PlaysOnAlike()
    {
        // The link map rechecks a link only when ground near its line changes, found through an index the snapshot
        // rebuilds on load: wall off the relay's line in both worlds after the load and both must lose the link.
        var s = RelayHeatTests.CreateScenario();
        var relay = RelayHeatTests.PlantRelay(s);
        BotTankAI?[] noBots = [null, null];
        Run(s.World, noBots, 0, 60, (_, _) => default);
        Assert.True(relay.LinkPoint!.IsPowered, "the relay must be linked when the snapshot is taken");
        Position Wall(int dx) => s.Base.Position + new Offset(dx, s.DirY * (Tweaks.Base.BaseSize / 2 + 15));

        var loaded = AssertPlaysOnAlike(s.World, noBots, () => RelayHeatTests.CreateScenario().World, ticks: 120,
            (_, _) => default, firstFrame: 60, before: (world, t) =>
            {
                if (t == 10)
                    for (int dx = -3; dx <= 3; dx++)
                        world.Terrain.SetPixel(Wall(dx), TerrainPixel.Rock);
            });
        Assert.False(loaded.Machines.Machines.Single(m => m.Type == MachineType.Relay).LinkPoint!.IsPowered,
            "the wall must cut the relay off");
    }

    [Fact]
    public void MachinesWreckedAfterTheLoad_PlayOnAlike()
    {
        var run = SimulationFingerprintTests.EnemyMachinesShotToPieces();
        run.Play(0, 30);
        Assert.Contains(run.World.Machines.Machines, m => m.Type == MachineType.Relay && m.IsAlive);

        var loaded = AssertPlaysOnAlike(run.World, run.Bots, run.BuildBare, ticks: run.Frames - 30, (f, t) => run.Input(f, t),
            firstFrame: 30);
        Assert.DoesNotContain(loaded.Machines.Machines, m => m.Type == MachineType.Relay);
    }

    [Fact]
    public void DiggingAndScorchAcrossTheLoad_WithAShotPending_PlayOnAlike()
    {
        // Regrowth keeps an appointment for each cell that can change; the load must carry the appointments, the
        // scorch of a wreck still fading and the dirt still growing, and a shot fired on the save tick that has not
        // flown yet.
        var world = TestHelpers.CreateSeededWorld();
        var home = world.TankList.Tanks[0].Base!;
        int dirY = world.Terrain.Height - home.Position.Y >= home.Position.Y ? 1 : -1;
        var wreck = new Machine(home.Position + new Offset(8, dirY * (Tweaks.Base.BaseSize / 2 + 12)), MachineType.Harvester,
            home.Color);
        world.Machines.Add(wreck);
        world.PlantMachine(wreck, home);
        const int SaveAt = 120;
        Script digAndShoot = (frame, i) => i != 0 ? default : new ControllerOutput
        {
            MoveSpeed = new Offset(frame > 160 ? 1 : 0, frame < 200 ? dirY : 0),
            ShootPrimary = frame < SaveAt + 20 && frame % 4 == (SaveAt - 1) % 4,
            AimDirection = new DirectionF(frame % 8 < 4 ? 1 : -1, dirY),
        };
        BotTankAI?[] noBots = [null, null];
        Run(world, noBots, 0, 60, digAndShoot);
        wreck.TakeDamage(10_000);
        Run(world, noBots, 60, SaveAt - 60, digAndShoot);
        long scorchAtSave = ScorchLeft(world);
        Assert.True(scorchAtSave > 0, "there must be scorch fading when the snapshot is taken");
        Assert.True(PendingShots(world) > 0, "a shot must be pending when the snapshot is taken");

        var loaded = AssertPlaysOnAlike(world, noBots, () => TestHelpers.CreateSeededWorld(), ticks: 300, digAndShoot,
            firstFrame: SaveAt);
        Assert.True(ScorchLeft(loaded) < scorchAtSave, "the scorch must fade after the load");
    }

    [Fact]
    public void MachineCarriedAcrossTheLoad_IsPlantedAlike()
    {
        // A harvester built in the base and carried out: the tank's hand holds a machine already in the machine list,
        // so the load must give back one object in both places, not two copies.
        var world = TestHelpers.CreateSeededWorld();
        var home = world.TankList.Tanks[0].Base!.Position;
        int dirY = world.Terrain.Height - home.Y >= home.Y ? 1 : -1;
        Script carryOut = (frame, i) => i != 0 ? default : new ControllerOutput
        {
            MoveSpeed = new Offset(0, frame is > 10 and < 90 ? dirY : 0),
            BuildSecondary = frame is 5 or 100,
        };
        BotTankAI?[] noBots = [null, null];
        Run(world, noBots, 0, 40, carryOut);
        Assert.True(world.TankList.Tanks[0].IsCarryingMachine, "the tank must carry a machine when the snapshot is taken");

        var loaded = AssertPlaysOnAlike(world, noBots, () => TestHelpers.CreateSeededWorld(), ticks: 120, carryOut,
            firstFrame: 40);
        Assert.False(loaded.TankList.Tanks[0].IsCarryingMachine, "the second press must plant it");
        Assert.Contains(loaded.Machines.Machines, m => m.Type == MachineType.Harvester && m.State != MachineState.Transporting);
    }

    /// <summary>How much scorch is left: the intensity of every scorched cell, added up.</summary>
    private static long ScorchLeft(World world)
    {
        long left = 0;
        for (int i = 0; i < world.Terrain.Width * world.Terrain.Height; i++)
            if (Pixel.IsScorched(world.Terrain[i]))
                left += world.Terrain.GetScorchIntensity(i);
        return left;
    }

    /// <summary>Shots fired this tick, which join the flight list at the start of the next.</summary>
    private static int PendingShots(World world) =>
        ((System.Collections.IList)typeof(ProjectileList)
            .GetField("_pending", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(world.Projectiles)!).Count;
}
