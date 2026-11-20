using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Input;
using Emberward.Core.Match;
using Emberward.Core.Terrain;
using Emberward.Core.Types;
using static Emberward.Tests.SnapshotRoundTrip;

namespace Emberward.Tests;

/// <summary>
/// Turrets (DESIGN.md §4.9, <see cref="TurretAction"/>): a turret on the energy network shoots the nearest enemy tank in
/// range and in sight, pays each shot in charge that becomes heat beside it, slows as that ground heats, and holds fire
/// when it is cut off or its base is dark. Its tier is fed before the economy's.
/// </summary>
public class TurretTests
{
    private const int EnemyGap = 12;

    private static RelayHeatTests.Scenario Scenario(bool blockLineOfSight = false) =>
        RelayHeatTests.CreateScenario(blockLineOfSight: blockLineOfSight, energyNetwork: true, machineUpgrades: true);

    /// <summary>A spot in the scenario's tunnel inside the base's own coverage, <paramref name="cells"/> past the base wall.</summary>
    private static Position Outside(RelayHeatTests.Scenario s, int cells) =>
        s.Base.Position + new Offset(0, s.DirY * (Tweaks.Base.BaseSize / 2 + 1 + cells));

    private static Machine Plant(RelayHeatTests.Scenario s, MachineType type, Position at)
    {
        var machine = new Machine(at, type, s.Tank.Color);
        s.World.Machines.Add(machine);
        s.World.PlantMachine(machine, s.Base);
        return machine;
    }

    /// <summary>The scenario's other tank, standing at <paramref name="at"/> once its respawn shield has gone.</summary>
    private static Tank Enemy(RelayHeatTests.Scenario s, Position at)
    {
        var enemy = s.World.TankList.Tanks[1];
        enemy.Position = at;
        for (int i = 0; i < 20 * Tweaks.Perf.TargetFps && enemy.SpawnProtectionTicks > 0; i++)
            s.World.Advance(_ => default);
        Assert.Equal(0, enemy.SpawnProtectionTicks);
        return enemy;
    }

    /// <summary>A turret planted 8 cells out (covered by the base) with an enemy <paramref name="gap"/> cells further along.</summary>
    private static (RelayHeatTests.Scenario S, Machine Turret, Tank Enemy) Duel(int gap = EnemyGap)
    {
        var s = Scenario();
        var turret = Plant(s, MachineType.Turret, Outside(s, 8));
        var enemy = Enemy(s, turret.Position + new Offset(0, s.DirY * gap));
        return (s, turret, enemy);
    }

    /// <summary>Advances <paramref name="ticks"/> ticks and returns the ticks (from 0) on which a turret fired.</summary>
    private static List<int> FiringTicks(World world, int ticks, Func<int, ControllerOutput>? input = null)
    {
        var fired = new List<int>();
        for (int t = 0; t < ticks; t++)
        {
            world.Advance(input ?? (_ => default));
            foreach (var e in world.StepEvents)
                if (e.Kind == StepEventKind.TurretFired)
                    fired.Add(t);
        }
        return fired;
    }

    private static int[] Gaps(List<int> ticks) => ticks.Zip(ticks.Skip(1), (a, b) => b - a).ToArray();

    /// <summary>The tank buys path <paramref name="key"/> for the turret beside it: one press, then a release.</summary>
    private static void Buy(RelayHeatTests.Scenario s, Machine turret, int key)
    {
        s.Tank.Position = turret.Position + new Offset(0, -s.DirY * 5);
        s.Base.Store.Ore = Math.Max(s.Base.Store.Ore, 100);
        int before = s.World.MachineUpgradeOf(turret).Level;
        s.World.Advance(i => i == 0 ? new ControllerOutput { UpgradeMachine = true, Upgrade = key } : default);
        s.World.Advance(_ => default);
        Assert.Equal(before + 1, s.World.MachineUpgradeOf(turret).Level);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FifthKey_BuildsATurret_OnlyInEnergyMatches(bool energyNetwork)
    {
        var s = RelayHeatTests.CreateScenario(energyNetwork: energyNetwork);
        s.Tank.Hold.Ore = Tweaks.Turret.OreCost;
        s.Tank.Position = Outside(s, 8);
        var press = new ControllerOutput { BuildQuinary = true };
        s.World.Advance(i => i == 0 ? press : default);
        Assert.Equal(energyNetwork ? MachineType.Turret : (MachineType?)null, s.Tank.CarriedMachineType);

        s.World.Advance(_ => default);
        s.World.Advance(i => i == 0 ? press : default);
        Assert.Null(s.Tank.CarriedMachineType);
        if (energyNetwork)
            Assert.True(Assert.Single(s.World.Machines.Machines).IsStanding);
        else
            Assert.Equal(Tweaks.Turret.OreCost, s.Tank.Hold.Ore);
    }

    [Fact]
    public void ATurret_ShootsAnEnemyInRangeAndSight()
    {
        var (s, turret, enemy) = Duel();
        int health = enemy.Reactor.Health;

        var fired = new List<StepEvent>();
        for (int t = 0; t < 3 * Tweaks.Turret.FireIntervalTicks; t++)
        {
            s.World.Advance(_ => default);
            fired.AddRange(s.World.StepEvents.ToArray().Where(e => e.Kind == StepEventKind.TurretFired));
        }

        Assert.True(s.World.MachinePowerOf(turret)!.Value.IsFed, "the base's coverage links the turret to its island");
        Assert.True(fired.Count >= 2, $"it should fire about once in {Tweaks.Turret.FireIntervalTicks} ticks, fired {fired.Count}");
        Assert.All(fired, e =>
        {
            Assert.Equal(turret.Position, e.Position);
            Assert.Equal(turret.OwnerColor, e.Color);
            Assert.Equal(MachineType.Turret, e.Machine);
            Assert.Equal(enemy.Color, e.Count);
        });
        Assert.True(enemy.Reactor.Health < health, "its bullets should reach the enemy");
        Assert.Equal(0, turret.Heading.X);
        Assert.Equal(Math.Sign(s.DirY), Math.Sign(turret.Heading.Y));
    }

    [Fact]
    public void TargetOf_PicksTheNearestEnemyInSight_TheEarliestBuiltTankOnATie()
    {
        var size = new Size(240, 180);
        var world = new World(size, rules: new MatchRules { TerrainRegrowth = false, EnergyNetwork = true });
        var terrain = new TerrainGrid(size);
        terrain.Fill(TerrainPixel.Blank);
        world.Initialize(terrain, [new Position(40, 40), new Position(200, 40), new Position(120, 145)]);
        var tanks = world.TankList;
        var at = new Position(120, 80);
        var turret = new Machine(at, MachineType.Turret, ownerColor: 0);
        var (first, second) = (tanks.Tanks[1], tanks.Tanks[2]);
        first.Position = at + new Offset(-10, 0);
        second.Position = at + new Offset(10, 0);
        for (int i = 0; i < 20 * Tweaks.Perf.TargetFps && (first.SpawnProtectionTicks > 0 || second.SpawnProtectionTicks > 0); i++)
            world.Advance(_ => default);
        int range = Tweaks.Turret.Range;

        // The same distance: the earlier in spawn order, whichever side it is on.
        Assert.Same(first, TurretAction.TargetOf(turret, range, tanks, world.Terrain));
        first.Position = at + new Offset(10, 0);
        second.Position = at + new Offset(-10, 0);
        Assert.Same(first, TurretAction.TargetOf(turret, range, tanks, world.Terrain));

        // Nearer wins.
        second.Position = at + new Offset(0, 9);
        Assert.Same(second, TurretAction.TargetOf(turret, range, tanks, world.Terrain));

        // Behind rock it is not seen, and the other is.
        for (int y = 3; y <= 5; y++)
            for (int x = -2; x <= 2; x++)
                world.Terrain.SetPixel(at + new Offset(x, y), TerrainPixel.Rock);
        Assert.Same(first, TurretAction.TargetOf(turret, range, tanks, world.Terrain));

        // Out of range, nothing; its own side's tank never.
        first.Position = at + new Offset(range + 1, 0);
        Assert.Null(TurretAction.TargetOf(turret, range, tanks, world.Terrain));
        first.Position = at + new Offset(range, 0);
        Assert.Same(first, TurretAction.TargetOf(turret, range, tanks, world.Terrain));
        tanks.Tanks[0].Position = at + new Offset(-3, 0);
        Assert.Same(first, TurretAction.TargetOf(turret, range, tanks, world.Terrain));
    }

    [Fact]
    public void ATurretOutsideItsNetwork_HoldsFire()
    {
        // The rock across the tunnel shuts the base's coverage out: nothing links the turret.
        var s = Scenario(blockLineOfSight: true);
        var turret = Plant(s, MachineType.Turret, Outside(s, 22));
        Enemy(s, turret.Position + new Offset(0, s.DirY * EnemyGap));

        Assert.Empty(FiringTicks(s.World, 4 * Tweaks.Turret.FireIntervalTicks));
        Assert.False(s.World.MachinePowerOf(turret)!.Value.IsFed);
        Assert.False(s.World.Energy!.IsOnIsland(turret));
    }

    [Fact]
    public void ATurretOfADarkBase_HoldsFire_UntilTheBaseRestarts()
    {
        var (s, turret, _) = Duel();
        s.Base.DrawCharge(s.Base.Charge);

        Assert.Empty(FiringTicks(s.World, 4 * Tweaks.Turret.FireIntervalTicks));
        Assert.True(s.World.IsBaseDark(s.Base));

        s.Base.StoreCharge(Tweaks.Base.RestartCharge);
        Assert.NotEmpty(FiringTicks(s.World, 4 * Tweaks.Turret.FireIntervalTicks));
        Assert.True(s.World.Energy!.IsOnIsland(turret));
    }

    [Fact]
    public void Firing_HeatsTheTurretsGround_AndHeatAndChargeStillBalance()
    {
        var (s, turret, _) = Duel();
        var ledger = s.World.Terrain.Ledger;
        float before = TurretAction.SenseTemperature(s.World.Terrain, turret.Position);
        double start = s.World.SumSystemThermalEnergy() + s.World.SumSystemCharge();
        double startExternal = ledger.Injected - ledger.TotalSunk;
        double maxResidual = 0.0;
        int shots = 0;
        for (int t = 0; t < 6 * Tweaks.Perf.TargetFps; t++)
        {
            s.World.Advance(_ => default);
            shots += s.World.StepEvents.ToArray().Count(e => e.Kind == StepEventKind.TurretFired);
            double now = s.World.SumSystemThermalEnergy() + s.World.SumSystemCharge();
            double residual = (now - start) - (ledger.Injected - ledger.TotalSunk - startExternal);
            maxResidual = Math.Max(maxResidual, Math.Abs(residual));
        }
        float after = TurretAction.SenseTemperature(s.World.Terrain, turret.Position);

        // Its bullets and the heat they leave kill the enemy in about three seconds.
        Assert.True(shots >= 5, $"it should have kept firing, fired {shots}");
        Assert.True(after > before + 5f, $"its shots should warm its ground: {before:0.0} -> {after:0.0}");
        Assert.True(maxResidual < 1.0, $"heat and charge must add up. residual={maxResidual:0.0000}");
    }

    [Fact]
    public void AHotTurret_FiresMoreSlowly()
    {
        int interval = Tweaks.Turret.FireIntervalTicks;
        Assert.Equal(interval, TurretAction.IntervalAt(interval, Tweaks.Turret.SlowTemperature));
        Assert.Equal((int)(interval * Tweaks.Turret.MaxSlowdown), TurretAction.IntervalAt(interval, Tweaks.Turret.MaxSlowTemperature));
        Assert.Equal((int)(interval * Tweaks.Turret.MaxSlowdown), TurretAction.IntervalAt(interval, 1000f));
        float halfway = (Tweaks.Turret.SlowTemperature + Tweaks.Turret.MaxSlowTemperature) / 2f;
        Assert.InRange(TurretAction.IntervalAt(interval, halfway), interval + 1, (int)(interval * Tweaks.Turret.MaxSlowdown) - 1);

        var (cool, _, _) = Duel();
        var (hot, turret, _) = Duel();
        hot.World.Terrain.AddHeatRadius(turret.Position, 400, 4);
        Assert.True(TurretAction.SenseTemperature(hot.World.Terrain, turret.Position) > Tweaks.Turret.MaxSlowTemperature);

        int[] coolGaps = Gaps(FiringTicks(cool.World, 4 * interval));
        int[] hotGaps = Gaps(FiringTicks(hot.World, 4 * interval));
        Assert.NotEmpty(coolGaps);
        Assert.All(coolGaps, gap => Assert.Equal(interval, gap));
        Assert.True(hot.World.Machines.Machines.Single(m => m.Type == MachineType.Turret).IsStalled);
        Assert.True(hotGaps.Length < coolGaps.Length, "a hot turret fires fewer shots in the same time");
        Assert.All(hotGaps, gap => Assert.True(gap > 2 * interval, $"a hot turret waits longer between shots: {gap}"));
    }

    /// <summary>
    /// The tier-ordering case tranche 1 could not reach: an island whose store cannot pay every tier feeds its turrets
    /// (the Turrets tier) and starves the economy tier.
    /// </summary>
    [Fact]
    public void AStoreThatCannotPayEveryTier_FeedsTurretsBeforeTheEconomy()
    {
        // A harvester stands for the economy tier: it pumps no heat, so the engine adds nothing to the store meanwhile.
        var s = Scenario();
        var harvester = Plant(s, MachineType.Harvester, Outside(s, 3));
        var turret = Plant(s, MachineType.Turret, Outside(s, 8));
        FiringTicks(s.World, Tweaks.Perf.TargetFps);
        var energy = s.World.Energy!;
        Assert.True(energy.IsOnIsland(turret));
        Assert.True(s.World.MachinePowerOf(harvester)!.Value.IsFed);

        // The turret wants charge, and the store holds only what the turret takes in a tick.
        energy.Spend(turret, 1000.0, s.World.Terrain);
        double before = turret.Charge;
        s.Base.DrawCharge(s.Base.Charge - Tweaks.Turret.TransferPerTick);
        s.World.Advance(_ => default);
        energy.Settle(turret, s.World.Terrain);

        Assert.True(s.World.MachinePowerOf(turret)!.Value.IsFed, "the turrets tier is paid first");
        Assert.True(turret.Charge > before, "the turret was topped up");
        Assert.False(s.World.MachinePowerOf(harvester)!.Value.IsFed, "the economy tier is starved");
    }

    [Fact]
    public void Rapid_FiresFaster_AndEachShotSpendsMore()
    {
        var plain = MachineUpgrades.TurretStatsOf(default);
        var rapid1 = MachineUpgrades.TurretStatsOf(new MachineUpgradeLevel(MachineUpgradePath.Rapid, 1));
        var rapid2 = MachineUpgrades.TurretStatsOf(new MachineUpgradeLevel(MachineUpgradePath.Rapid, 2));
        Assert.True(plain.IntervalTicks > rapid1.IntervalTicks && rapid1.IntervalTicks > rapid2.IntervalTicks);
        Assert.True(plain.ShotCharge < rapid1.ShotCharge && rapid1.ShotCharge < rapid2.ShotCharge);
        Assert.Equal((plain.Range, plain.Damage), (rapid2.Range, rapid2.Damage));

        var (s, turret, _) = Duel();
        Buy(s, turret, 1);
        Buy(s, turret, 1);
        Assert.Equal(new MachineUpgradeLevel(MachineUpgradePath.Rapid, 2), s.World.MachineUpgradeOf(turret));
        int[] gaps = Gaps(FiringTicks(s.World, 4 * Tweaks.Turret.FireIntervalTicks));
        Assert.NotEmpty(gaps);
        Assert.All(gaps, gap => Assert.Equal(rapid2.IntervalTicks, gap));
    }

    [Fact]
    public void Long_ReachesFurther_HitsHarder_AndFiresMoreSlowly()
    {
        var plain = MachineUpgrades.TurretStatsOf(default);
        var long1 = MachineUpgrades.TurretStatsOf(new MachineUpgradeLevel(MachineUpgradePath.LongRange, 1));
        Assert.Equal(plain.Range + Tweaks.Turret.LongRangePerLevel, long1.Range);
        Assert.True(long1.Damage > plain.Damage);
        Assert.True(long1.IntervalTicks > plain.IntervalTicks);

        // Beyond its plain range it does not fire; with Long it does, harder and less often.
        var (s, turret, enemy) = Duel(gap: Tweaks.Turret.Range + 4);
        Assert.Empty(FiringTicks(s.World, 3 * Tweaks.Turret.FireIntervalTicks));
        Buy(s, turret, 2);
        int health = enemy.Reactor.Health;
        var fired = FiringTicks(s.World, 4 * long1.IntervalTicks);
        Assert.True(fired.Count >= 2);
        Assert.All(Gaps(fired), gap => Assert.Equal(long1.IntervalTicks, gap));
        Assert.Equal(0, (health - enemy.Reactor.Health) % long1.Damage);
        Assert.True(health - enemy.Reactor.Health >= long1.Damage, "its bullets should reach the enemy");
    }

    [Fact]
    public void ATurretMidFight_SurvivesASnapshot()
    {
        var (s, turret, _) = Duel();
        Buy(s, turret, 2);
        FiringTicks(s.World, Tweaks.Turret.FireIntervalTicks + 3);
        Assert.True(turret.TurretCooldown > 0 || s.World.Projectiles.Count > 0);
        AssertPlaysOnAlike(s.World, [null, null], () => Scenario().World, ticks: 3 * Tweaks.Turret.FireIntervalTicks,
            (_, _) => default);
    }
}
