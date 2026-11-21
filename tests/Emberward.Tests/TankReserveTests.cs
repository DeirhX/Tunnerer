using Emberward.Core;
using Emberward.Core.Bots;
using Emberward.Core.Config;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Input;
using Emberward.Core.Match;
using Emberward.Core.Terrain;
using Emberward.Core.Types;
using static Emberward.Tests.SnapshotRoundTrip;

namespace Emberward.Tests;

/// <summary>
/// The tank's reserve (DESIGN.md §4.9, <see cref="TankReserveSystem"/>): the off-network clock, which the recall key
/// buys back by burning carried pyrite. The stages of running out are in <see cref="TankReserveStageTests"/>.
/// </summary>
public class TankReserveTests
{
    internal static readonly ControllerOutput Burn = new() { Recall = true };

    /// <summary>How many times faster than in a match the clock runs in <see cref="OpenWorld"/>, to keep the tests short.</summary>
    internal const int Speedup = 4;

    // Far from the base at (25, 36) on open ground: off the network.
    private static readonly Position Far = new(200, 150);

    /// <summary>
    /// An open map with one base, the reserve rule on and the tank parked far out, off its network. Its clock runs
    /// <see cref="Speedup"/> times as fast as in a match; the stages still go by the floor.
    /// </summary>
    internal static (World World, Tank Tank) OpenWorld(bool energyNetwork = false)
    {
        var size = TestHelpers.DefaultMapSize;
        var ground = new TerrainGrid(size);
        ground.Fill(TerrainPixel.Blank);
        var settings = SimulationSettings.FromTweaks();
        settings = settings with { TankOffNetworkHeatPerTick = settings.TankOffNetworkHeatPerTick * Speedup };
        var world = new World(size, rules: new MatchRules
        {
            TerrainRegrowth = false,
            TankReserve = true,
            EnergyNetwork = energyNetwork,
        }, simulationSettings: settings);
        world.Initialize(ground, [new Position(25, 36)]);
        var tank = world.TankList.Tanks[0];
        tank.Position = Far;
        return (world, tank);
    }

    internal static void Advance(World world, int ticks, ControllerOutput input = default)
    {
        for (int i = 0; i < ticks; i++)
            world.Advance(_ => input);
    }

    /// <summary>Advances until <paramref name="tank"/>'s reserve reaches <paramref name="stage"/>.</summary>
    internal static void AdvanceTo(World world, Tank tank, TankReserveStage stage)
    {
        int limit = (int)(Tweaks.Tank.HeatSafeMax / Tweaks.Tank.OffNetworkHeatPerSecond + 5) * Tweaks.Perf.TargetFps;
        for (int i = 1; i <= limit; i++)
        {
            world.Advance(_ => default);
            if (world.ReserveOf(tank).Stage == stage)
                return;
        }
        throw new Xunit.Sdk.XunitException($"the reserve never reached {stage}");
    }

    internal static int Count(World world, StepEventKind kind) =>
        world.StepEvents.ToArray().Count(e => e.Kind == kind && e.Color == 0);

    private static Position AlongTunnel(RelayHeatTests.Scenario s, int beyondDoor) =>
        s.Base.Position + new Offset(0, s.DirY * (Tweaks.Base.BaseSize / 2 + 1 + beyondDoor));

    [Fact]
    public void ABurn_ExtendsTheReserve_AndCoolsTheTank()
    {
        var s = RelayHeatTests.CreateScenario(tankReserve: true);
        s.Tank.Position = AlongTunnel(s, 48);
        Advance(s.World, Tweaks.Perf.TargetFps * 40);
        s.Tank.Hold.Ore = 10;
        Advance(s.World, 2);
        float heat = s.Tank.Heat, floor = s.Tank.OffNetworkFloor;
        float left = s.World.ReserveOf(s.Tank).SecondsLeft;
        Assert.True(s.Tank.IsOffNetwork);
        Assert.True(floor > 35f, $"forty seconds out the floor has climbed. floor={floor:0.0}");

        s.World.Advance(_ => Burn);

        Assert.Equal(9, s.Tank.OreHeld);
        Assert.Equal(1, Count(s.World, StepEventKind.ReserveBurned));
        Assert.False(s.Tank.IsDead, "with ore aboard the recall key burns; it does not recall");
        float tick = Tweaks.Tank.OffNetworkHeatPerSecond / Tweaks.Perf.TargetFps;
        Assert.Equal(floor + tick - Tweaks.Reserve.FloorDropPerOre, s.Tank.OffNetworkFloor, precision: 3);
        float bought = Tweaks.Reserve.FloorDropPerOre / Tweaks.Tank.OffNetworkHeatPerSecond;
        Assert.Equal(left + bought - 1f / Tweaks.Perf.TargetFps, s.World.ReserveOf(s.Tank).SecondsLeft, precision: 3);

        // The tank, no longer held up by the floor, sheds into the ground; without the burn it would only climb.
        Advance(s.World, Tweaks.Perf.TargetFps);
        Assert.True(s.Tank.Heat < heat - 3f, $"the tank should cool to the lower floor. {heat:0.0} -> {s.Tank.Heat:0.0}");
    }

    [Fact]
    public void HoldingTheKey_BurnsAUnitEveryRepeat()
    {
        var (world, tank) = OpenWorld();
        Advance(world, Tweaks.Perf.TargetFps * 40 / Speedup);
        tank.Hold.Ore = 20;

        int burned = 0;
        for (int i = 0; i < Tweaks.Perf.TargetFps; i++)
        {
            world.Advance(_ => Burn);
            burned += Count(world, StepEventKind.ReserveBurned);
        }

        int expected = (Tweaks.Perf.TargetFps + Tweaks.Reserve.BurnRepeatTicks - 1) / Tweaks.Reserve.BurnRepeatTicks;
        Assert.Equal(expected, burned);
        Assert.Equal(20 - expected, tank.OreHeld);
    }

    [Fact]
    public void ABurn_NeedsAUnitsWorthToBuyBack_AndTheNetworkOff()
    {
        var s = RelayHeatTests.CreateScenario(tankReserve: true);
        s.Tank.Position = AlongTunnel(s, 6);   // inside the base's coverage
        s.Tank.Hold.Ore = 5;
        Advance(s.World, 4, Burn);
        Assert.Equal(5, s.Tank.OreHeld);
        Assert.False(s.World.ReserveOf(s.Tank).IsActive);

        // Just outside, the floor has not climbed a unit's worth yet: the key burns nothing until it has.
        var (world, tank) = OpenWorld();
        tank.Hold.Ore = 5;
        int ticksToAUnit = (int)(Tweaks.Reserve.FloorDropPerOre / world.Settings.TankOffNetworkHeatPerTick);
        Advance(world, ticksToAUnit - 2, Burn);
        Assert.Equal(5, tank.OreHeld);
        Assert.False(world.ReserveOf(tank).CanBurn, "a burn would buy back less than its worth");
        Advance(world, 4, Burn);
        Assert.Equal(4, tank.OreHeld);
        Assert.True(tank.OffNetworkFloor < 1f, $"floor={tank.OffNetworkFloor:0.00}");
    }

    [Fact]
    public void Burns_LeaveTheLedgerBalanced()
    {
        var s = RelayHeatTests.CreateScenario(tankReserve: true);
        s.Tank.Position = AlongTunnel(s, 48);
        s.Tank.Hold.Ore = 30;
        var world = s.World;
        double startEnergy = world.SumSystemThermalEnergy();
        double startNet = world.Terrain.Ledger.NetExternal;
        double worst = 0.0;
        for (int i = 0; i < Tweaks.Perf.TargetFps * 60; i++)
        {
            // Out for half a minute, then burn now and then, firing as it goes.
            bool burn = i > Tweaks.Perf.TargetFps * 30 && i % 40 < 8;
            world.Advance(_ => new ControllerOutput { Recall = burn, ShootPrimary = i % 50 == 0 });
            double residual = world.SumSystemThermalEnergy() - startEnergy - (world.Terrain.Ledger.NetExternal - startNet);
            worst = Math.Max(worst, Math.Abs(residual));
        }

        Assert.True(s.Tank.OreHeld < 30, "the scenario should burn");
        Assert.True(worst < 0.5, $"energy created or destroyed outside the ledger: {worst:0.0000}");
    }

    [Fact]
    public void TheRecallKey_BurnsWhileThereIsOre_ThenRecalls()
    {
        var (world, tank) = OpenWorld();
        AdvanceTo(world, tank, TankReserveStage.HeatDeath);
        tank.Hold.Ore = 1;
        Assert.True(tank.CanRecall);
        Assert.False(world.RecallOffered(tank), "with ore aboard the key burns");

        world.Advance(_ => Burn);
        Assert.False(tank.IsDead);
        Assert.Equal(0, tank.OreHeld);
        Assert.True(world.ReserveOf(tank).Stage < TankReserveStage.HeatDeath, "the burn bought time back");

        AdvanceTo(world, tank, TankReserveStage.HeatDeath);
        Assert.True(world.RecallOffered(tank));
        world.Advance(_ => Burn);
        Assert.Contains(world.StepEvents.ToArray(), e => e.Kind == StepEventKind.TankRespawned);
        Assert.Equal(tank.Base!.SpawnPosition, tank.Position);
    }

    [Fact]
    public void ABot_BurnsWhatItCarries_OnceDegraded()
    {
        var (world, tank) = OpenWorld();
        var bot = new BotTankAI(seed: 5);
        tank.Hold.Ore = 3;
        Advance(world, Tweaks.Perf.TargetFps * 10 / Speedup);
        Assert.False(bot.GetInput(tank, null, world).Recall, "with time to spare the bot keeps its cargo");

        AdvanceTo(world, tank, TankReserveStage.Degraded);
        Assert.True(bot.GetInput(tank, null, world).Recall);
    }

    [Fact]
    public void WithoutTheRule_NothingChanges()
    {
        var world = TestHelpers.CreateSeededWorld();
        var tank = world.TankList.Tanks[0];
        tank.Position = new Position(160, 100);
        tank.Hold.Ore = 5;
        Advance(world, 12, Burn);

        Assert.False(world.ReserveOf(tank).IsActive);
        Assert.Equal(5, tank.OreHeld);
        Assert.Equal(TankReserveEffects.Normal, TankReserveEffects.For(TankReserveStage.None));
    }

    [Fact]
    public void AReserveMidBurn_PlaysOnAlike()
    {
        static World Build() => TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false, tankReserve: true);
        var world = Build();
        BotTankAI?[] noBots = [null, null];
        Script drive = (frame, i) => i != 0 ? default : new ControllerOutput
        {
            MoveSpeed = new Offset(frame % 40 < 20 ? 1 : -1, 0),
            Recall = frame > 400 && frame % 30 < 9,
            ShootPrimary = frame % 7 == 0,
        };
        // Out in the middle of the map, far from either base, digging back and forth.
        var tank = world.TankList.Tanks[0];
        tank.Position = new Position(160, 100);
        Run(world, noBots, 0, 380, drive);
        tank.Hold.Ore = 12;
        Run(world, noBots, 380, 30, drive);
        Assert.True(world.ReserveOf(tank).IsActive, "the tank must be off its network when the snapshot is taken");

        var loaded = AssertPlaysOnAlike(world, noBots, Build, ticks: 400, drive, firstFrame: 410);
        Assert.True(loaded.TankList.Tanks[0].OreHeld < 12, "the run should burn after the load");
    }
}
