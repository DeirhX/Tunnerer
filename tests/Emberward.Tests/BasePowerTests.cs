namespace Emberward.Tests;

using Emberward.Core;
using Emberward.Core.Collision;
using Emberward.Core.Config;
using Emberward.Core.Entities;
using Emberward.Core.Entities.Bases;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Entities.Projectiles;
using Emberward.Core.Entities.Tanks;
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

    private const int Half = BaseLayout.Half;

    /// <summary>A shell from just outside the west wall, <paramref name="dy"/> cells off its middle: the gate at 0, wall past 5.</summary>
    private static void FireAtWestSide(World world, TankBase target, int owner, int dy = 0, int damage = Tweaks.Weapon.BulletDamage)
    {
        world.Projectiles.Add(Projectile.CreateBullet(target.Position + new Offset(-Half - 1, dy), new VectorF(1, 0), owner, damage));
        world.Advance(_ => default);
    }

    /// <summary>
    /// A shell from a corner of the courtyard along the diagonal, the one line that reaches the core. Returns the
    /// ticks it took to land, or -1 if it never damaged the base.
    /// </summary>
    private static int FireAtCore(World world, TankBase target, int owner)
    {
        world.Projectiles.Add(Projectile.CreateBullet(
            target.Position + BaseLayout.NearestCorner(-1, -1), new VectorF(1, 1), owner));
        // The shell crosses the gap between the baffles and the floor round the core before it lands.
        for (int tick = 1; tick <= BaseLayout.Lane; tick++)
        {
            world.Advance(_ => default);
            if (world.StepEvents.ToArray().Any(e => e.Kind is StepEventKind.BaseDamaged or StepEventKind.BaseDestroyed))
                return tick;
        }
        return -1;
    }

    /// <summary>Spends the whole gate shield of <paramref name="target"/> with one shell at its west gate.</summary>
    private static void BreakShield(World world, TankBase target, int owner) =>
        FireAtWestSide(world, target, owner, damage: (int)Tweaks.Keep.ShieldCapacity);

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
        world.Terrain.SetPixel(ore, TerrainPixel.Pyrite);
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
        Assert.Equal(TerrainPixel.Pyrite, world.Terrain.GetPixelRaw(ore));
        Assert.Equal(Tweaks.Base.InitialHealth - 100, (int)home.Reactor.Health);

        home.StoreCharge(1);
        world.Advance(_ => default);
        Assert.False(world.IsBaseDark(home));
        world.Advance(_ => default);
        Assert.True((int)home.Reactor.Health > Tweaks.Base.InitialHealth - 100);
        Assert.True((int)tank.Reactor.Health > Tweaks.Tank.InitialHealth - 100);
    }

    [Fact]
    public void AShellAtAWall_DoesNoDamage()
    {
        var world = CreateWorld();
        var target = world.TankBases.Bases[1];

        FireAtWestSide(world, target, owner: 0, dy: BaseLayout.GateHalf + 2);

        Assert.Equal(Tweaks.Base.InitialHealth, (int)target.Reactor.Health);
        Assert.Equal(1.0, world.BasePowerOf(target)!.Value.ShieldFill);
        Assert.DoesNotContain(world.StepEvents.ToArray(), e => e.Kind is StepEventKind.BaseDamaged or StepEventKind.BaseShieldHit);
        Assert.Contains(world.StepEvents.ToArray(), e => e.Kind == StepEventKind.BulletImpact && e.Target == ImpactTarget.Terrain);
    }

    [Fact]
    public void AnEnemyShellAtAShutGate_StopsThere_AndWearsTheShieldDown()
    {
        var world = CreateWorld();
        var target = world.TankBases.Bases[1];
        // No charge to spare: the shield does not recharge in the tick it is hit.
        target.DrawCharge(target.Charge - Tweaks.Keep.ShieldReserveCharge);

        FireAtWestSide(world, target, owner: 0);

        Assert.Equal(Tweaks.Base.InitialHealth, (int)target.Reactor.Health);
        Assert.Equal(1.0 - Tweaks.Weapon.BulletDamage / Tweaks.Keep.ShieldCapacity, world.BasePowerOf(target)!.Value.ShieldFill, 9);
        Assert.True(world.AreGatesShut(target));
        var hit = Assert.Single(world.StepEvents.ToArray(), e => e.Kind == StepEventKind.BaseShieldHit);
        Assert.Equal(target.Color, hit.Color);
        Assert.Equal(target.Position + new Offset(-Half, 0), hit.Position);
        Assert.Contains(world.StepEvents.ToArray(), e => e.Kind == StepEventKind.BulletImpact && e.Target == ImpactTarget.Base);
    }

    [Fact]
    public void TheOwnersShell_PassesItsOwnShutGate()
    {
        var world = CreateWorld();
        var target = world.TankBases.Bases[1];

        FireAtWestSide(world, target, owner: target.Color);

        Assert.Equal(1.0, world.BasePowerOf(target)!.Value.ShieldFill);
        Assert.DoesNotContain(world.StepEvents.ToArray(), e => e.Kind == StepEventKind.BulletImpact);
        Assert.Equal(1, world.Projectiles.Count);
    }

    [Fact]
    public void ASpentShield_OpensTheGates_UntilItIsPartlyBack()
    {
        var world = CreateWorld();
        var target = world.TankBases.Bases[1];

        BreakShield(world, target, owner: 0);

        Assert.False(world.AreGatesShut(target));
        Assert.Contains(world.StepEvents.ToArray(), e => e.Kind == StepEventKind.BaseShieldBroken);

        // Open, a gate stops no shell: the next one flies in and lands on the baffle behind the gate.
        FireAtWestSide(world, target, owner: 0);
        Assert.DoesNotContain(world.StepEvents.ToArray(), e => e.Kind is StepEventKind.BaseShieldHit or StepEventKind.BaseShieldBroken);

        int ticks = (int)Math.Ceiling(Tweaks.Keep.ShieldRestoreFill * Tweaks.Keep.ShieldCapacity / Tweaks.Keep.ShieldRechargePerTick);
        bool restored = false;
        for (int i = 0; i < ticks + 1 && !restored; i++)
        {
            Assert.False(world.AreGatesShut(target));
            world.Advance(_ => default);
            restored = world.StepEvents.ToArray().Any(e => e.Kind == StepEventKind.BaseShieldRestored);
        }
        Assert.True(restored);
        Assert.True(world.AreGatesShut(target));
        Assert.Equal(Tweaks.Base.InitialHealth, (int)target.Reactor.Health);
    }

    [Fact]
    public void TheShield_RechargesFromTheStore_AsReactorHeat_DownToTheReserve()
    {
        var world = CreateWorld();
        var target = world.TankBases.Bases[1];
        BreakShield(world, target, owner: 0);
        double charge = target.Charge, shield = world.BasePowerOf(target)!.Value.ShieldFill;
        double converted = world.Terrain.Ledger.ConvertedToCharge;

        world.Advance(_ => default);

        double spent = Tweaks.Keep.ShieldRechargePerTick * Tweaks.Keep.ChargePerShieldPoint;
        Assert.Equal(charge - spent, target.Charge, 6);
        Assert.Equal(shield + Tweaks.Keep.ShieldRechargePerTick / Tweaks.Keep.ShieldCapacity,
            world.BasePowerOf(target)!.Value.ShieldFill, 9);
        // The charge became heat in the reactor (which the radiator then vents), and the ledger has it.
        Assert.Equal(converted - spent, world.Terrain.Ledger.ConvertedToCharge, 6);

        // With nothing above the reserve, the shield stays where it is: defending never blacks the base out.
        target.DrawCharge(target.Charge - Tweaks.Keep.ShieldReserveCharge);
        shield = world.BasePowerOf(target)!.Value.ShieldFill;
        world.Advance(_ => default);
        Assert.Equal(Tweaks.Keep.ShieldReserveCharge, target.Charge);
        Assert.Equal(shield, world.BasePowerOf(target)!.Value.ShieldFill);
        Assert.False(world.IsBaseDark(target));
    }

    [Fact]
    public void ADarkBase_DoesNotRechargeItsShield()
    {
        var world = CreateWorld();
        var target = world.TankBases.Bases[1];
        BreakShield(world, target, owner: 0);
        target.DrawCharge(target.Charge);
        world.Advance(_ => default);
        Assert.True(world.IsBaseDark(target));
        target.StoreCharge(Tweaks.Base.RestartCharge - 1);
        double shield = world.BasePowerOf(target)!.Value.ShieldFill;

        TestSimulation.AdvanceIdle(world, frames: 20);

        Assert.Equal(shield, world.BasePowerOf(target)!.Value.ShieldFill);
        Assert.False(world.AreGatesShut(target));
    }

    [Theory]
    [InlineData(false, Tweaks.Weapon.BulletDamage)]
    [InlineData(true, 200)]
    public void AShellFromACourtyardCorner_DamagesTheCore_MoreWhileTheBaseIsDark(bool dark, int expectedDamage)
    {
        var world = CreateWorld();
        var target = world.TankBases.Bases[1];
        if (dark)
            target.DrawCharge(target.Charge);

        int ticks = FireAtCore(world, target, owner: 0);

        Assert.True(ticks > 0, "The shell never reached the core.");
        var hit = Assert.Single(world.StepEvents.ToArray(), e => e.Kind == StepEventKind.BaseDamaged);
        Assert.Equal(target.Color, hit.Color);
        Assert.Contains(world.StepEvents.ToArray(), e => e.Kind == StepEventKind.BulletImpact && e.Target == ImpactTarget.Base);
        // A dark base repairs nothing; a lit one has repaired a little in the tick the shell landed.
        int repaired = dark ? 0 : Tweaks.Base.HealthRegen;
        Assert.Equal(Tweaks.Base.InitialHealth - expectedDamage + repaired, (int)target.Reactor.Health);
    }

    [Fact]
    public void AShutGate_StopsAnEnemyTank_AndLetsItsOwnerThrough()
    {
        var world = CreateWorld();
        var raider = world.TankList.Tanks[0];
        var target = world.TankBases.Bases[1];
        var owner = world.TankList.Tanks[1];
        var outside = target.Position + new Offset(-Half - 6, 0);
        var drive = new ControllerOutput { MoveSpeed = new Offset(1, 0) };

        raider.Position = outside;
        for (int i = 0; i < 20; i++)
            world.Advance(slot => slot == 0 ? drive : default);
        // Its hull is 7 cells wide: it stops with its nose against the gate.
        Assert.Equal(target.Position.X - Half - TankSpriteShape.Size / 2 - 1, raider.Position.X);
        Assert.Equal(StepObstacle.Entity, world.ProbeTankStep(raider, new Offset(1, 0)));

        // The owner drives out through the same gate.
        raider.Position = new Position(2, 2);
        owner.Position = target.Position + new Offset(-BaseLayout.Lane, 0);
        for (int i = 0; i < 12; i++)
            world.Advance(slot => slot == 1 ? new ControllerOutput { MoveSpeed = new Offset(-1, 0) } : default);
        Assert.Equal(target.Position.X - BaseLayout.Lane - 12, owner.Position.X);

        // Spent, the shield lets the raider in. The owner waits by the east gate, out of the way.
        owner.Position = target.Position + new Offset(BaseLayout.Lane, 0);
        raider.Position = outside;
        BreakShield(world, target, owner: 0);
        for (int i = 0; i < 10; i++)
            world.Advance(slot => slot == 0 ? drive : default);
        Assert.True(target.IsInside(raider.Position));
    }

    [Fact]
    public void AnEnemyCoreImpact_DestroysTheBaseAndEndsTheSide()
    {
        var world = CreateWorld(energyNetwork: false);
        var target = world.TankBases.Bases[1];
        target.TakeDamage(Tweaks.Base.InitialHealth - 50);
        Assert.Equal(-1, FireAtCore(world, target, owner: 1));
        Assert.False(target.IsDestroyed);

        Assert.True(FireAtCore(world, target, owner: 0) > 0);
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
        Assert.Contains(world.StepEvents.ToArray(), e => e.Kind == StepEventKind.RestartOreBurned && e.Count == 15);
    }

    [Fact]
    public void APartialChargeInABlackout_SurvivesASnapshot()
    {
        var original = CreateWorld();
        var home = original.TankBases.Bases[0];
        // A shield that is partly spent is part of what a late joiner must get.
        FireAtWestSide(original, home, owner: 1, damage: 12_345);
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
        Assert.Equal(tank.Base!.SpawnPosition, tank.Position);
        Assert.Equal(0, tank.OreHeld);
        Assert.Contains(world.StepEvents.ToArray(), e => e.Kind == StepEventKind.TankRespawned);
    }
}
