namespace Emberward.Tests;

using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Entities;
using Emberward.Core.Entities.Bases;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Entities.Projectiles;
using Emberward.Core.Input;
using Emberward.Core.Match;
using Emberward.Core.Resources;
using Emberward.Core.Snapshots;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

public class BasePowerTests
{
    private static World CreateWorld(bool energyNetwork = true)
    {
        var size = new Size(100, 72);
        var ground = new TerrainGrid(size);
        ground.Fill(TerrainPixel.Blank);
        var world = new World(size, rules: new MatchRules { EnergyNetwork = energyNetwork, TerrainRegrowth = false });
        world.Initialize(ground, [new Position(25, 36), new Position(75, 36)]);
        return world;
    }

    private static void FireAtWall(World world, TankBase target, int owner)
    {
        int half = Tweaks.Base.BaseSize / 2;
        world.Projectiles.Add(Projectile.CreateBullet(target.Position + new Offset(-half - 1, 0), new VectorF(1, 0), owner));
        world.Advance(_ => default);
    }

    [Fact]
    public void DarkBase_StopsRepairsAndHarvesters_UntilTheRestartThreshold()
    {
        var world = CreateWorld();
        var tank = world.TankList.Tanks[0];
        var home = tank.Base!;
        var harvester = new Machine(home.Position + new Offset(0, -21), MachineType.Harvester, tank.Color);
        world.Machines.Add(harvester);
        world.PlantMachine(harvester, home);
        Position ore = harvester.Position + new Offset(3, 0);
        world.Terrain.SetPixel(ore, TerrainPixel.Emberite);
        home.TakeDamage(100);
        tank.TakeDamage(100, DamageCause.Overheat);
        home.DrawCharge(home.Charge);
        world.Advance(_ => default);

        Assert.True(world.IsBaseDark(home));
        Assert.Equal(Tweaks.Base.InitialHealth - 100, (int)home.Reactor.Health);
        Assert.Equal(Tweaks.Tank.InitialHealth - 100, (int)tank.Reactor.Health);
        Assert.Equal(0, world.GetNetworkStatus(home).HarvestersWorking);

        home.StoreCharge(Tweaks.Base.RestartCharge - 1);
        TestSimulation.AdvanceIdle(world, frames: 20);
        Assert.True(world.IsBaseDark(home));
        Assert.Equal(Tweaks.Base.RestartCharge - 1.0, home.Charge);
        Assert.Equal(TerrainPixel.Emberite, world.Terrain.GetPixelRaw(ore));
        Assert.Equal(Tweaks.Base.InitialHealth - 100, (int)home.Reactor.Health);

        home.StoreCharge(1);
        world.Advance(_ => default);
        Assert.False(world.IsBaseDark(home));
        Assert.Equal(30.0, world.BasePowerOf(home)!.Value.ShieldWarmupSeconds);
        Assert.Equal(0.0, world.BasePowerOf(home)!.Value.ShieldFraction);
        world.Advance(_ => default);
        Assert.True((int)home.Reactor.Health > Tweaks.Base.InitialHealth - 100);
        Assert.True((int)tank.Reactor.Health > Tweaks.Tank.InitialHealth - 100);
        Assert.True(world.BasePowerOf(home)!.Value.ShieldFraction > 0.0);
    }

    [Theory]
    [InlineData(1.0, 32)]
    [InlineData(0.5, 96)]
    [InlineData(0.25, 128)]
    [InlineData(0.0, 200)]
    public void WallImpact_ScalesDamageWithCharge(double fill, int expectedDamage)
    {
        var world = CreateWorld();
        var target = world.TankBases.Bases[1];
        target.DrawCharge(target.Charge);
        target.StoreCharge(target.ChargeCapacity * fill);

        FireAtWall(world, target, owner: 0);

        int repaired = fill > 0.0 ? Tweaks.Base.HealthRegen : 0;
        Assert.Equal(Tweaks.Base.InitialHealth - expectedDamage + repaired, (int)target.Reactor.Health);
        var hit = Assert.Single(world.StepEvents.ToArray(), e => e.Kind == StepEventKind.BaseDamaged);
        Assert.Equal(target.Color, hit.Color);
        Assert.Contains(world.StepEvents.ToArray(), e => e.Kind == StepEventKind.BulletImpact && e.Target == ImpactTarget.Base);
    }

    [Fact]
    public void EnemyWallImpact_DestroysTheBaseAndEndsTheSide()
    {
        var world = CreateWorld(energyNetwork: false);
        var target = world.TankBases.Bases[1];
        target.TakeDamage(Tweaks.Base.InitialHealth - 50);
        FireAtWall(world, target, owner: 1);
        Assert.False(target.IsDestroyed);

        FireAtWall(world, target, owner: 0);
        Assert.True(target.IsDestroyed);
        Assert.False(target.LinkPoint!.IsPowered);
        Assert.Contains(world.StepEvents.ToArray(), e => e.Kind == StepEventKind.BaseDestroyed);
        Assert.Equal(MatchWinReason.LastBaseStanding, world.MatchWinner?.Reason);
        Assert.Equal(MatchOutcome.Defeat, world.MatchOutcomeFor(world.TankList.Tanks[1]));
        Assert.Equal(TimeSpan.Zero, world.TankList.Tanks[1].RespawnRemaining);
    }

    [Fact]
    public void Docking_BurnsCarriedOreAndRecordsTheCharge()
    {
        var world = CreateWorld();
        var tank = world.TankList.Tanks[0];
        var home = tank.Base!;
        int banked = home.Store.Ore;
        home.DrawCharge(home.Charge);
        tank.Hold.Add(19);

        world.Advance(_ => default);

        Assert.Equal(4, tank.OreHeld);
        Assert.Equal(banked, home.Store.Ore);
        Assert.Equal((double)Tweaks.Base.RestartCharge, home.Charge);
        Assert.Equal(home.Charge, world.Terrain.Ledger.RestartOreCharge);
        Assert.True(world.Terrain.Ledger.Injected >= home.Charge);
        Assert.Equal(home.Charge, world.Terrain.Ledger.ConvertedToCharge);
        Assert.False(world.IsBaseDark(home));
        Assert.Equal(30.0, world.BasePowerOf(home)!.Value.ShieldWarmupSeconds);
        Assert.Contains(world.StepEvents.ToArray(), e => e.Kind == StepEventKind.RestartOreBurned && e.Count == 15);
    }

    [Fact]
    public void APartialChargeInABlackout_SurvivesASnapshot()
    {
        var original = CreateWorld();
        var home = original.TankBases.Bases[0];
        home.DrawCharge(home.Charge);
        original.Advance(_ => default);
        home.StoreCharge(25_000);
        original.Advance(_ => default);

        var restored = CreateWorld();
        WorldSnapshot.Load(restored, WorldSnapshot.Save(original, [null, null]));
        var restoredHome = restored.TankBases.Bases[0];

        Assert.True(restored.IsBaseDark(restoredHome));
        Assert.Equal(original.BasePowerOf(home), restored.BasePowerOf(restoredHome));
        Assert.Equal(WorldStateHash.ComputeSync(original), WorldStateHash.ComputeSync(restored));
    }

    [Theory]
    [InlineData(1.0, 3.0)]
    [InlineData(0.5, 9.0)]
    [InlineData(0.0, 15.0)]
    public void TankRespawnDelay_UsesItsBaseCharge(double fill, double seconds)
    {
        var world = CreateWorld();
        var tank = world.TankList.Tanks[0];
        var home = tank.Base!;
        home.DrawCharge(home.Charge);
        home.StoreCharge(home.ChargeCapacity * fill);
        tank.TakeDamage(tank.Reactor.HealthCapacity, DamageCause.Overheat);

        world.Advance(_ => default);

        Assert.Equal(TimeSpan.FromSeconds(seconds) - world.Settings.AdvanceStep, tank.RespawnRemaining);
        Assert.False(world.IsMatchOver);
    }

    [Fact]
    public void Recall_RespawnsImmediatelyAndDropsCargo()
    {
        var world = CreateWorld(energyNetwork: false);
        var tank = world.TankList.Tanks[0];
        var offNetwork = new Position(1, 1);
        tank.Position = offNetwork;
        // Exhaust the existing reserve by accelerating the heat-floor increase while keeping the tank alive.
        var settings = world.Settings with { TankOffNetworkHeatPerTick = Tweaks.Tank.HeatSafeMax };
        var ground = new TerrainGrid(world.Terrain.Size);
        ground.Fill(TerrainPixel.Blank);
        world = new World(ground.Size, rules: new MatchRules { TerrainRegrowth = false }, simulationSettings: settings);
        world.Initialize(ground, [new Position(25, 36)]);
        tank = world.TankList.Tanks[0];
        tank.Position = offNetwork;
        tank.Hold.Add(3);
        world.Advance(_ => default);
        Assert.True(tank.CanRecall);

        world.Advance(_ => new ControllerOutput { Recall = true });

        Assert.False(tank.IsDead);
        Assert.Equal(tank.Base!.Position, tank.Position);
        Assert.Equal(0, tank.OreHeld);
        Assert.Contains(world.StepEvents.ToArray(), e => e.Kind == StepEventKind.TankRespawned);
    }
}
