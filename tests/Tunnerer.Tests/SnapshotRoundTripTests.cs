using Tunnerer.Core;
using Tunnerer.Core.Bots;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Entities.Tanks;
using Tunnerer.Core.Input;
using Tunnerer.Core.Match;
using Tunnerer.Core.Snapshots;
using Tunnerer.Core.Types;

namespace Tunnerer.Tests;

/// <summary>
/// A world saved mid-match and loaded into a freshly built one plays on exactly as the original does
/// (docs/late-join-design.md §10.3, item 2). Each scenario is built to reach state that shows only later: an
/// explosion after the load, a harvester on its rounds, a tank under spawn protection, a build key held across the
/// load, a core tap mid-hold, bots mid-manoeuvre. Every tick after the load both worlds give the same full
/// fingerprint (both temperature fields cell by cell, the ledger) and the same sync digest, and at the end they save
/// to the same bytes.
/// </summary>
public class SnapshotRoundTripTests
{
    private delegate ControllerOutput Script(int frame, int tank);

    [Fact]
    [Trait("Category", "Slow")]
    public void BotsMidMatch_PlayOnAlike()
    {
        var original = BotWorld();
        BotTankAI?[] bots = [new BotTankAI(seed: 11), new BotTankAI(seed: 22)];
        Run(original, bots, 0, 1200, (_, _) => default);

        AssertPlaysOnAlike(original, bots, BotWorld, ticks: 600, (_, _) => default);
    }

    [Fact]
    public void LinkNetworkAndHarvester_PlayOnAlike()
    {
        var s = RelayHeatTests.CreateScenario();
        s.World.Terrain.AddHeatRadius(s.RelayPos, 150, 8);
        RelayHeatTests.PlantRelay(s);
        Plant(s, MachineType.Pylon, Tweaks.Base.BaseSize / 2 + 6);
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = 3; dx <= 5; dx++)
                s.World.Terrain.SetPixel(s.Base.Position + new Offset(dx, s.DirY * (Tweaks.Base.BaseSize / 2 + 18 + dy)),
                    Core.Terrain.TerrainPixel.Emberite);
        Plant(s, MachineType.Harvester, Tweaks.Base.BaseSize / 2 + 18);
        Script script = (frame, tank) => tank != 0 ? default : new ControllerOutput
        {
            MoveSpeed = new Offset(0, frame < 200 ? s.DirY : -s.DirY),
            ShootPrimary = frame % 11 == 0,
            AimDirection = new DirectionF(0, s.DirY),
        };
        BotTankAI?[] noBots = [null, null];
        Run(s.World, noBots, 0, 150, script);
        Assert.Contains(s.World.Machines.Machines, m => m.Type == MachineType.Harvester && m.IsAlive);

        AssertPlaysOnAlike(s.World, noBots, () => RelayHeatTests.CreateScenario().World, ticks: 250, script, firstFrame: 150);
    }

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
                        world.Terrain.SetPixel(Wall(dx), Core.Terrain.TerrainPixel.Rock);
            });
        Assert.False(loaded.Machines.Machines.Single(m => m.Type == MachineType.Relay).LinkPoint!.IsPowered,
            "the wall must cut the relay off");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void MachinesWreckedAfterTheLoad_PlayOnAlike()
    {
        var s = RelayHeatTests.CreateScenario();
        var enemy = s.World.TankBases.Bases.First(b => b.Color != s.Tank.Color);
        var relay = new Machine(s.RelayPos, MachineType.Relay, enemy.Color);
        var harvester = new Machine(s.RelayPos + new Offset(0, s.DirY * 10), MachineType.Harvester, enemy.Color);
        foreach (var machine in new[] { relay, harvester })
        {
            s.World.Machines.Add(machine);
            s.World.PlantMachine(machine, enemy);
        }
        Script script = (frame, tank) => tank != 0 ? default : new ControllerOutput
        {
            MoveSpeed = new Offset(0, frame < 60 ? s.DirY : 0),
            ShootPrimary = frame % 5 == 0,
            AimDirection = new DirectionF(0, s.DirY),
        };
        BotTankAI?[] noBots = [null, null];
        Run(s.World, noBots, 0, 30, script);
        Assert.True(relay.IsAlive, "the relay must still stand when the snapshot is taken");

        var loaded = AssertPlaysOnAlike(s.World, noBots, () => RelayHeatTests.CreateScenario().World, ticks: 370, script,
            firstFrame: 30);
        Assert.DoesNotContain(loaded.Machines.Machines, m => m.Type == MachineType.Relay);
    }

    [Fact]
    [Trait("Category", "Slow")]
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
    [Trait("Category", "Slow")]
    public void CoreTapMidHold_PlaysOnAlike()
    {
        var world = WorldWithCore();
        typeof(CoreTap).GetProperty(nameof(CoreTap.HoldSeconds))!
            .SetValue(world.CoreTapOf(world.TankList.Tanks[0].Base), 1.5);
        BotTankAI?[] bots = [new BotTankAI(seed: 5), new BotTankAI(seed: 6)];
        Run(world, bots, 0, 240, (_, _) => default);

        AssertPlaysOnAlike(world, bots, WorldWithCore, ticks: 240, (_, _) => default);
    }

    [Fact]
    public void Saving_IsCanonical_AndLoadingTwiceGivesTheSameWorld()
    {
        var world = BotWorld();
        BotTankAI?[] bots = [new BotTankAI(seed: 11), null];
        Run(world, bots, 0, 300, (_, _) => default);

        byte[] first = WorldSnapshot.Save(world, bots), second = WorldSnapshot.Save(world, bots);
        Assert.Equal(first, second);

        var once = BotWorld();
        var twice = BotWorld();
        var botsOnce = WorldSnapshot.Load(once, first);
        WorldSnapshot.Load(twice, WorldSnapshot.Save(once, botsOnce));
        Assert.Equal(first, WorldSnapshot.Save(twice, botsOnce));
        Assert.Null(botsOnce[1]);
    }

    [Fact]
    public void Compression_GivesBackTheSameBytes()
    {
        var world = BotWorld();
        BotTankAI?[] bots = [new BotTankAI(seed: 11), new BotTankAI(seed: 22)];
        Run(world, bots, 0, 200, (_, _) => default);
        byte[] raw = WorldSnapshot.Save(world, bots);

        byte[] compressed = WorldSnapshot.Compress(raw);

        Assert.True(compressed.Length < raw.Length / 2, $"{compressed.Length} of {raw.Length} bytes");
        Assert.Equal(raw, WorldSnapshot.Decompress(compressed));
        Assert.Throws<InvalidDataException>(() => WorldSnapshot.Decompress(compressed, maxBytes: raw.Length - 1));
    }

    [Fact]
    public void ASnapshotThatDoesNotFit_IsRefused()
    {
        var world = BotWorld();
        byte[] raw = WorldSnapshot.Save(world, [null, null]);

        Assert.Throws<InvalidDataException>(() => WorldSnapshot.Load(TestHelpers.CreateSeededWorld(size: new Size(160, 100)), raw));
        Assert.ThrowsAny<Exception>(() => WorldSnapshot.Load(BotWorld(), raw[..(raw.Length / 2)]));
        byte[] foreign = (byte[])raw.Clone();
        foreign[0] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => WorldSnapshot.Load(BotWorld(), foreign));
    }

    /// <summary>
    /// Saves <paramref name="original"/>, loads it into <paramref name="build"/>'s world, checks the loaded world
    /// saves to the same bytes, then steps both <paramref name="ticks"/> times, comparing every tick.
    /// <paramref name="before"/> does the same to both worlds before each tick.
    /// </summary>
    private static World AssertPlaysOnAlike(World original, BotTankAI?[] bots, Func<World> build, int ticks, Script script,
        int firstFrame = 0, Action<World, int>? before = null)
    {
        byte[] saved = WorldSnapshot.Save(original, bots);
        var loaded = build();
        var loadedBots = WorldSnapshot.Load(loaded, saved);
        Assert.Equal(saved, WorldSnapshot.Save(loaded, loadedBots));
        AssertSame(original, bots, loaded, loadedBots, "on load");

        for (int t = 0; t < ticks; t++)
        {
            before?.Invoke(original, t);
            before?.Invoke(loaded, t);
            Run(original, bots, firstFrame + t, 1, script);
            Run(loaded, loadedBots, firstFrame + t, 1, script);
            AssertSame(original, bots, loaded, loadedBots, $"{t + 1} ticks after the load");
        }
        Assert.True(WorldSnapshot.Save(original, bots).AsSpan().SequenceEqual(WorldSnapshot.Save(loaded, loadedBots)),
            $"the worlds save to different bytes {ticks} ticks after the load");
        return loaded;
    }

    private static void AssertSame(World a, BotTankAI?[] botsA, World b, BotTankAI?[] botsB, string when)
    {
        ulong fa = SimulationFingerprintTests.Fingerprint(a), fb = SimulationFingerprintTests.Fingerprint(b);
        Assert.True(fa == fb, $"Fingerprints differ {when}: 0x{fa:X16} against 0x{fb:X16}.");
        SyncDigest da = WorldStateHash.ComputeSync(a, botsA), db = WorldStateHash.ComputeSync(b, botsB);
        Assert.True(da == db, $"Sync digests differ {when}: {da} against {db}.");
    }

    private static void Run(World world, BotTankAI?[] bots, int firstFrame, int frames, Script script)
    {
        var tanks = world.TankList.Tanks;
        for (int f = 0; f < frames; f++)
        {
            int frame = firstFrame + f;
            world.Advance(i => bots[i] is { } bot ? bot.GetInput(tanks[i], tanks[1 - i], world) : script(frame, i));
        }
    }

    private static World BotWorld() =>
        TestHelpers.CreateSeededWorld(hotRockPocketCount: 2, frostRockPocketCount: 1, seedOre: true);

    private static World WorldWithCore()
    {
        var size = new Size(480, 300);
        var (terrain, spawns) = new Core.LevelGen.ToastGenerator().Generate(size, seed: 42, playerCount: 2);
        var world = new World(size, simulationSeed: 42, new MatchRules { HotRockPockets = 3, CoreTapWins = true });
        world.Initialize(terrain, spawns, decorationSeed: 43);
        return world;
    }

    private static void Plant(RelayHeatTests.Scenario s, MachineType type, int distanceFromBase)
    {
        var machine = new Machine(s.Base.Position + new Offset(0, s.DirY * distanceFromBase), type, s.Tank.Color);
        s.World.Machines.Add(machine);
        s.World.PlantMachine(machine, s.Base);
    }
}
