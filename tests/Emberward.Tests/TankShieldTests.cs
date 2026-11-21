using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Entities;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Match;
using Emberward.Core.Types;

namespace Emberward.Tests;

/// <summary>The tank's shield in front of its hull (<see cref="MatchRules.TankShield"/>, DESIGN.md §4.7).</summary>
public class TankShieldTests
{
    private static (World World, Tank Tank) Shielded()
    {
        var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false, tankShield: true);
        return (world, world.TankList.Tanks[0]);
    }

    private static void Step(World world, int ticks)
    {
        for (int i = 0; i < ticks; i++)
            world.Advance(_ => TestSimulation.Idle);
    }

    [Fact]
    public void AFreshTank_HasAFullShield()
    {
        var (world, tank) = Shielded();

        var view = world.TankShieldOf(tank);

        Assert.True(view.Active);
        Assert.Equal(Tweaks.TankShield.Capacity, view.Shield);
        Assert.Equal(1f, view.ShieldFill);
    }

    [Fact]
    public void WithoutTheRule_TheViewIsInactive_AndTheHullTakesEveryHit()
    {
        var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false);
        var tank = world.TankList.Tanks[0];
        int before = tank.Reactor.Health;

        tank.TakeDamage(50, DamageCause.EnemyFire);

        Assert.False(world.TankShieldOf(tank).Active);
        Assert.Equal(before - 50, tank.Reactor.Health);
        Assert.Equal(new DamageTaken(0, 50), tank.LastDamageTaken);
    }

    [Fact]
    public void ADamage_TakesTheShieldFirst_AndTheOverflowGoesToTheHull()
    {
        var (world, tank) = Shielded();
        int hull = tank.Reactor.Health;

        tank.TakeDamage(Tweaks.TankShield.Capacity - 100, DamageCause.EnemyFire);
        Assert.Equal(hull, tank.Reactor.Health);
        Assert.Equal(100, world.TankShieldOf(tank).Shield);
        Assert.False(tank.LastDamageTaken.ShieldBroken);

        tank.TakeDamage(160, DamageCause.EnemyFire);
        Assert.Equal(new DamageTaken(100, 60, ShieldBroken: true), tank.LastDamageTaken);
        Assert.Equal(hull - 60, tank.Reactor.Health);
        Assert.Equal(0, world.TankShieldOf(tank).Shield);
    }

    [Fact]
    public void TheShield_WaitsAfterAHit_ThenRefills()
    {
        var (world, tank) = Shielded();
        tank.TakeDamage(200, DamageCause.EnemyFire);

        Step(world, Tweaks.TankShield.RegenDelayTicks - 1);
        Assert.Equal(Tweaks.TankShield.Capacity - 200, world.TankShieldOf(tank).Shield);

        // The delay ends and the first refill comes in the same tick: RegenDelayTicks ticks after the hit.
        Step(world, 1);
        Assert.Equal(Tweaks.TankShield.Capacity - 200 + Tweaks.TankShield.RegenPerTick, world.TankShieldOf(tank).Shield);

        Step(world, 200 / Tweaks.TankShield.RegenPerTick + 1);
        Assert.Equal(Tweaks.TankShield.Capacity, world.TankShieldOf(tank).Shield);
    }

    [Fact]
    public void AnotherHit_RestartsTheDelay()
    {
        var (world, tank) = Shielded();
        tank.TakeDamage(100, DamageCause.EnemyFire);
        Step(world, Tweaks.TankShield.RegenDelayTicks - 1);

        tank.TakeDamage(1, DamageCause.EnemyFire);

        Assert.Equal(Tweaks.TankShield.RegenDelayTicks, world.TankShieldOf(tank).RegenDelayTicks);
        Step(world, Tweaks.TankShield.RegenDelayTicks - 1);
        Assert.Equal(Tweaks.TankShield.Capacity - 101, world.TankShieldOf(tank).Shield);
    }

    [Fact]
    public void TheHull_DoesNotRegenerate()
    {
        var (world, tank) = Shielded();
        tank.TakeDamage(Tweaks.TankShield.Capacity + 300, DamageCause.EnemyFire);
        int hull = tank.Reactor.Health;
        tank.Position = new Position(60, 100);

        Step(world, Tweaks.TankShield.RegenDelayTicks + 200);

        Assert.Equal(hull, tank.Reactor.Health);
    }

    [Theory]
    [InlineData(DamageCause.BaseDestroyed)]
    [InlineData(DamageCause.Recalled)]
    [InlineData(DamageCause.Overheat)]
    [InlineData(DamageCause.Frostbite)]
    public void BaseLossRecallHeatAndCold_GoStraightToTheHull(DamageCause cause)
    {
        var (_, tank) = Shielded();

        tank.TakeDamage(tank.Reactor.HealthCapacity, cause);

        Assert.True(tank.IsDead);
        Assert.Equal(0, tank.LastDamageTaken.Shield);
    }

    [Fact]
    public void OverheatDamage_LeavesTheShieldAlone()
    {
        var (world, tank) = Shielded();
        int hull = tank.Reactor.Health;

        tank.TakeDamage(30, DamageCause.Overheat);

        Assert.Equal(hull - 30, tank.Reactor.Health);
        Assert.Equal(Tweaks.TankShield.Capacity, world.TankShieldOf(tank).Shield);
    }

    [Fact]
    public void OffTheNetwork_TheShieldDoesNotRefill()
    {
        var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false, energyNetwork: true, tankShield: true);
        var tank = world.TankList.Tanks[0];
        // Far from any link and from the bases: the coverage rule has it off its network.
        for (int y = -30; y <= 30; y++)
            for (int x = -60; x <= 60; x++)
                world.Terrain.SetPixel(new Position(60, 100) + new Offset(x, y), Core.Terrain.TerrainPixel.Blank);
        tank.Position = new Position(60, 100);
        Step(world, 2);
        Assert.True(tank.IsOffNetwork);
        tank.TakeDamage(200, DamageCause.EnemyFire);

        Step(world, Tweaks.TankShield.RegenDelayTicks + 60);

        Assert.Equal(Tweaks.TankShield.Capacity - 200, world.TankShieldOf(tank).Shield);
    }

    [Fact]
    public void ARespawn_RestoresTheShield()
    {
        var (world, tank) = Shielded();
        tank.TakeDamage(Tweaks.TankShield.Capacity + tank.Reactor.Health, DamageCause.EnemyFire);
        Assert.True(tank.IsDead);

        tank.Spawn(world.Terrain.Ledger);

        Assert.Equal(Tweaks.TankShield.Capacity, world.TankShieldOf(tank).Shield);
        Assert.Equal(0, world.TankShieldOf(tank).RegenDelayTicks);
    }

    [Fact]
    public void SpawnProtection_StillBlocksEnemyFire()
    {
        var (world, tank) = Shielded();
        tank.TakeDamage(tank.Reactor.HealthCapacity, DamageCause.Recalled);
        tank.Spawn(world.Terrain.Ledger);

        Assert.False(tank.TakeDamage(10, DamageCause.EnemyFire));
        Assert.False(tank.LastDamageTaken.Any);
        Assert.Equal(Tweaks.TankShield.Capacity, world.TankShieldOf(tank).Shield);
    }

    [Fact]
    public void AHitOnTheShield_StillFlashesTheTank()
    {
        var (_, tank) = Shielded();

        tank.TakeDamage(50, DamageCause.EnemyFire);

        Assert.Equal(Tank.HitFlashDuration, tank.HitFlashTicks);
    }

    [Fact]
    public void TheShieldRule_IsPartOfTheRulesFingerprint_AndOfStandard()
    {
        Assert.True(MatchRules.Standard.TankShield);
        Assert.NotEqual(new MatchRules().Fingerprint(), new MatchRules { TankShield = true }.Fingerprint());
    }

    [Fact]
    public void TheStateHash_SeesTheShield()
    {
        var (a, tankA) = Shielded();
        var (b, _) = Shielded();
        Assert.Equal(WorldStateHash.Compute(a), WorldStateHash.Compute(b));

        tankA.TakeDamage(10, DamageCause.EnemyFire);

        Assert.NotEqual(WorldStateHash.Compute(a), WorldStateHash.Compute(b));
    }

    [Fact]
    public void Events_SplitAHitIntoShieldHullAndBroken()
    {
        var (_, tank) = Shielded();
        var log = new StepEventLog();
        var at = new Position(5, 6);

        tank.TakeDamage(100, DamageCause.EnemyFire);
        TankDamageEvents.Record(log, tank, at, hullEvent: true);
        Assert.Equal([StepEventKind.TankShieldHit, StepEventKind.TankHit], log.Events.ToArray().Select(e => e.Kind));
        Assert.Equal(at, log.Events[0].Position);
        Assert.Equal(100, log.Events[0].Count);

        log.Clear();
        tank.TakeDamage(Tweaks.TankShield.Capacity, DamageCause.EnemyFire);
        TankDamageEvents.Record(log, tank, at, hullEvent: true);
        Assert.Equal([StepEventKind.TankShieldHit, StepEventKind.TankShieldBroken, StepEventKind.TankHit],
            log.Events.ToArray().Select(e => e.Kind));
    }

    [Fact]
    public void ADeadTank_GetsAFullShieldBackAtOnce()
    {
        var (world, tank) = Shielded();
        tank.TakeDamage(Tweaks.TankShield.Capacity + tank.Reactor.Health, DamageCause.EnemyFire);
        Assert.True(tank.IsDead);

        Step(world, 1);

        Assert.Equal(Tweaks.TankShield.Capacity, world.TankShieldOf(tank).Shield);
    }

    [Fact]
    public void TheDelay_RunsOffTheNetwork_SoTheRefillStartsWhenItReturns()
    {
        var (world, tank) = Shielded();
        tank.TakeDamage(100, DamageCause.EnemyFire);
        Step(world, Tweaks.TankShield.RegenDelayTicks);

        // The tank is at its base, on its network: the refill began the tick the delay ended.
        Assert.Equal(0, world.TankShieldOf(tank).RegenDelayTicks);
        Assert.True(world.TankShieldOf(tank).Regenerating);
    }

    [Fact]
    public void Regenerating_IsFalseWhileTheShieldCannotRefill()
    {
        var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false, energyNetwork: true, tankShield: true);
        var tank = world.TankList.Tanks[0];
        for (int y = -30; y <= 30; y++)
            for (int x = -60; x <= 60; x++)
                world.Terrain.SetPixel(new Position(60, 100) + new Offset(x, y), Core.Terrain.TerrainPixel.Blank);
        tank.Position = new Position(60, 100);
        Step(world, 2);
        tank.TakeDamage(200, DamageCause.EnemyFire);

        Step(world, Tweaks.TankShield.RegenDelayTicks + 10);

        Assert.True(tank.IsOffNetwork);
        Assert.False(world.TankShieldOf(tank).Regenerating);
    }
}
