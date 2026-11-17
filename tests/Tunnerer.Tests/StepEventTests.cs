using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Entities.Projectiles;
using Tunnerer.Core.Input;
using Tunnerer.Core.Resources;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;
using Tunnerer.Core.Entities.Tanks;

namespace Tunnerer.Tests;

/// <summary>
/// <see cref="World.StepEvents"/> reports what happened during a step: each kind of event fires in a small scenario,
/// and the list starts empty again with every step.
/// </summary>
public class StepEventTests
{
    private static List<StepEvent> Step(World world, ControllerOutput tank0 = default)
    {
        world.Advance(i => i == 0 ? tank0 : default);
        return [.. world.StepEvents];
    }

    /// <summary>Steps until <paramref name="until"/> holds for an event, collecting every event on the way.</summary>
    private static List<StepEvent> StepUntil(World world, Func<StepEvent, bool> until, int maxSteps = 200,
        ControllerOutput tank0 = default)
    {
        var all = new List<StepEvent>();
        for (int i = 0; i < maxSteps; i++)
        {
            var events = Step(world, tank0);
            all.AddRange(events);
            if (events.Any(until))
                return all;
        }
        Assert.Fail($"no matching event in {maxSteps} steps; saw {string.Join(", ", all.Select(e => e.Kind).Distinct())}");
        return all;
    }

    private static int EnemyColor(World world, Tank tank) => world.TankList.Tanks.First(t => t.Color != tank.Color).Color;

    /// <summary>Fills a square of <paramref name="pixel"/> around <paramref name="center"/>.</summary>
    private static void Fill(TerrainGrid terrain, Position center, int half, TerrainPixel pixel, int fromDx = int.MinValue)
    {
        for (int dy = -half; dy <= half; dy++)
            for (int dx = Math.Max(-half, fromDx); dx <= half; dx++)
                terrain.SetPixel(center + new Offset(dx, dy), pixel);
    }

    /// <summary>The tank, moved out of its base into an open pocket of the scenario world.</summary>
    private static RelayHeatTests.Scenario InOpenPocket()
    {
        var s = RelayHeatTests.CreateScenario();
        Fill(s.World.Terrain, s.RelayPos, 12, TerrainPixel.Blank);
        s.Tank.Position = s.RelayPos;
        Step(s.World); // leaves the base: Undocked
        return s;
    }

    [Fact]
    public void Firing_RecordsTheShot_ThenTheImpactExplosionAndShrapnel()
    {
        var s = RelayHeatTests.CreateScenario();
        var fire = new ControllerOutput { ShootPrimary = true, AimDirection = new DirectionF(0, s.DirY) };

        var first = Step(s.World, fire);
        Assert.Contains(first, e => e.Kind == StepEventKind.ShotFired && e.Color == s.Tank.Color);

        var next = Step(s.World);
        Assert.DoesNotContain(next, e => e.Kind == StepEventKind.ShotFired);

        var later = StepUntil(s.World, e => e.Kind == StepEventKind.ShrapnelSpent);
        var impact = Assert.Single(next.Concat(later), e => e.Kind == StepEventKind.BulletImpact);
        Assert.Equal(ImpactTarget.Terrain, impact.Target);
        Assert.Equal(s.Tank.Color, impact.Color);
        Assert.Contains(next.Concat(later), e => e.Kind == StepEventKind.Explosion && e.Position == impact.Position);
    }

    [Fact]
    public void EventsOfAStep_AreClearedWhenTheNextBegins()
    {
        var s = InOpenPocket();
        var fire = new ControllerOutput { ShootPrimary = true, AimDirection = new DirectionF(0, s.DirY) };
        Step(s.World, fire);
        Assert.NotEqual(0, s.World.StepEvents.Length);

        // A quiet step later on: nothing moves, shoots or flies, and the player's tank is away from its base.
        for (int i = 0; i < 300 && s.World.Projectiles.Count > 0; i++)
            Step(s.World);
        // The enemy tank sits in its own base, being serviced, which is all that happens.
        Assert.All(Step(s.World), e => Assert.Equal((StepEventKind.BaseServiced, EnemyColor(s.World, s.Tank)), (e.Kind, e.Color)));
    }

    [Fact]
    public void EnemyBullet_IntoTank_RecordsImpactAndHit()
    {
        var s = InOpenPocket();
        int enemy = EnemyColor(s.World, s.Tank);
        s.World.Projectiles.Add(Projectile.CreateBullet(s.Tank.Position + new Offset(-10, 0),
            new VectorF(Tweaks.Weapon.CannonBulletSpeed, 0), enemy));

        var events = StepUntil(s.World, e => e.Kind == StepEventKind.TankHit);
        Assert.Contains(events, e => e.Kind == StepEventKind.TankHit && e.Color == s.Tank.Color);
        Assert.Contains(events, e => e.Kind == StepEventKind.BulletImpact && e.Target == ImpactTarget.Tank && e.Color == enemy);
    }

    [Fact]
    public void TankDeath_ThenRespawn_AndTheRespawnIsNotADocking()
    {
        var s = InOpenPocket();
        Position diedAt = s.Tank.Position;
        s.Tank.TakeDamage(10_000, DamageCause.Overheat);

        var died = Step(s.World);
        Assert.Contains(died, e => e.Kind == StepEventKind.TankDied && e.Color == s.Tank.Color && e.Position == diedAt);

        var after = StepUntil(s.World, e => e.Kind == StepEventKind.TankRespawned, maxSteps: 200);
        after.AddRange(Step(s.World));
        Assert.Equal(1, after.Count(e => e.Kind == StepEventKind.TankRespawned && e.Color == s.Tank.Color));
        Assert.DoesNotContain(after, e => e.Kind is StepEventKind.Docked or StepEventKind.Undocked or StepEventKind.TankDied);
    }

    [Fact]
    public void LeavingAndEnteringTheOwnBase_RecordsUndockedAndDocked()
    {
        var s = RelayHeatTests.CreateScenario();
        Assert.DoesNotContain(Step(s.World), e => e.Kind is StepEventKind.Docked or StepEventKind.Undocked);

        s.Tank.Position = s.RelayPos;
        Assert.Contains(Step(s.World), e => e.Kind == StepEventKind.Undocked && e.Color == s.Tank.Color);
        Assert.DoesNotContain(Step(s.World), e => e.Kind == StepEventKind.Undocked);

        s.Tank.Position = s.Base.Position;
        Assert.Contains(Step(s.World), e => e.Kind == StepEventKind.Docked && e.Color == s.Tank.Color);
    }

    [Fact]
    public void DamagedTankAtItsBase_IsServiced()
    {
        var s = RelayHeatTests.CreateScenario();
        Step(s.World);
        s.Tank.TakeDamage(Tweaks.Tank.HealthCapacity / 2, DamageCause.Overheat);
        Assert.Contains(Step(s.World), e => e.Kind == StepEventKind.BaseServiced && e.Color == s.Tank.Color);
    }

    [Fact]
    public void BuildingAndPlanting_RecordBuiltAndPlanted()
    {
        var s = RelayHeatTests.CreateScenario();
        s.Tank.Hold.Add(s.Tank.OreHoldCapacity);

        var built = Step(s.World, new ControllerOutput { BuildSecondary = true });
        Assert.Contains(built, e => e.Kind == StepEventKind.MachineBuilt && e.Machine == MachineType.Harvester && e.Color == s.Tank.Color);
        Step(s.World);

        s.Tank.Position = s.RelayPos;
        var planted = Step(s.World, new ControllerOutput { BuildSecondary = true });
        var plant = Assert.Single(planted, e => e.Kind == StepEventKind.MachinePlanted);
        Assert.Equal(MachineType.Harvester, plant.Machine);
        Assert.Equal(s.Tank.Color, plant.Color);
    }

    [Fact]
    public void RelayThatWouldNotLink_IsRefused()
    {
        var s = RelayHeatTests.CreateScenario(blockLineOfSight: true);
        s.Tank.Hold.Add(s.Tank.OreHoldCapacity);
        Step(s.World, new ControllerOutput { BuildPrimary = true });
        Step(s.World);

        s.Tank.Position = s.RelayPos;
        var events = Step(s.World, new ControllerOutput { BuildPrimary = true });
        Assert.Contains(events, e => e.Kind == StepEventKind.PlantRefused && e.Machine == MachineType.Relay);
        Assert.DoesNotContain(events, e => e.Kind == StepEventKind.MachinePlanted);
    }

    [Fact]
    public void BulletsIntoAnEnemyMachine_RecordDamageThenTheWreck()
    {
        var s = RelayHeatTests.CreateScenario();
        int enemy = EnemyColor(s.World, s.Tank);
        var relay = new Machine(s.RelayPos, MachineType.Relay, enemy);
        s.World.Machines.Add(relay);
        s.World.PlantMachine(relay, s.World.TankBases.Bases.First(b => b.Color == enemy));
        Assert.Contains(s.World.StepEvents.ToArray(), e => e.Kind == StepEventKind.MachinePlanted && e.Color == enemy);

        Projectile Fire()
        {
            var bullet = Projectile.CreateBullet(s.RelayPos + new Offset(0, -s.DirY * 8),
                new VectorF(0, s.DirY * Tweaks.Weapon.CannonBulletSpeed), s.Tank.Color);
            s.World.Projectiles.Add(bullet);
            return bullet;
        }

        Fire();
        var hit = StepUntil(s.World, e => e.Kind == StepEventKind.BulletImpact);
        Assert.Contains(hit, e => e.Kind == StepEventKind.BulletImpact && e.Target == ImpactTarget.Machine && e.Color == s.Tank.Color);
        Assert.Contains(hit, e => e.Kind == StepEventKind.MachineDamaged && e.Color == enemy && e.Machine == MachineType.Relay);
        Assert.DoesNotContain(hit, e => e.Kind == StepEventKind.MachineWrecked);

        relay.Reactor.Exhaust(new ReactorState(0, (int)relay.Reactor.Health - Tweaks.Weapon.BulletDamage));
        Fire();
        var wreck = StepUntil(s.World, e => e.Kind == StepEventKind.BulletImpact);
        Assert.Contains(wreck, e => e.Kind == StepEventKind.MachineWrecked && e.Color == enemy && e.Position == relay.Position);
        Assert.DoesNotContain(wreck, e => e.Kind == StepEventKind.MachineDamaged);
        Assert.Contains(wreck, e => e.Kind == StepEventKind.Explosion && e.Position == relay.Position);
    }

    [Fact]
    public void DrivingAndDigging_RecordMovedAndDug()
    {
        var s = InOpenPocket();
        Fill(s.World.Terrain, s.RelayPos, 12, TerrainPixel.Dirt, fromDx: 6);
        var right = new ControllerOutput { MoveSpeed = new Offset(1, 0) };

        Assert.Contains(Step(s.World, right), e => e.Kind == StepEventKind.TankMoved && e.Color == s.Tank.Color);
        var dug = StepUntil(s.World, e => e.Kind == StepEventKind.TankDug, maxSteps: 20, tank0: right)
            .First(e => e.Kind == StepEventKind.TankDug);
        Assert.Equal(s.Tank.Color, dug.Color);
        Assert.True(dug.Count > 0);
        Assert.False(dug.Torch);
    }

    [Fact]
    public void TorchingRock_RecordsTorchDigging_ButYieldsNoOre()
    {
        var s = InOpenPocket();
        Fill(s.World.Terrain, s.RelayPos, 12, TerrainPixel.Rock, fromDx: 5);
        var torch = new ControllerOutput { MoveSpeed = new Offset(1, 0), ShootPrimary = true, AimDirection = new DirectionF(1, 0) };

        var events = StepUntil(s.World, e => e.Kind == StepEventKind.TankDug && e.Torch, tank0: torch);
        for (int i = 0; i < 60; i++)
            events.AddRange(Step(s.World, torch));
        Assert.Contains(events, e => e.Kind == StepEventKind.TankDug && e.Torch && e.Color == s.Tank.Color);
        Assert.DoesNotContain(events, e => e.Kind == StepEventKind.OreCollected);
        Assert.Equal(0, s.Tank.OreHeld);
    }

    [Fact]
    public void DiggingOre_RecordsOreCollected()
    {
        var s = InOpenPocket();
        Fill(s.World.Terrain, s.RelayPos, 12, TerrainPixel.Emberite, fromDx: 5);
        var right = new ControllerOutput { MoveSpeed = new Offset(1, 0) };

        var collected = StepUntil(s.World, e => e.Kind == StepEventKind.OreCollected, tank0: right)
            .First(e => e.Kind == StepEventKind.OreCollected);
        Assert.Equal(s.Tank.Color, collected.Color);
        Assert.Equal(Tweaks.Ore.PlainCellUnits, collected.Count);
    }

    [Fact]
    public void AHarvesterBankingOre_RecordsOreHarvested()
    {
        var s = RelayHeatTests.CreateScenario();
        HarvesterHeatTests.SeedOre(s);
        RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(0, s.DirY * -15));
        var harvester = new Machine(s.RelayPos, MachineType.Harvester, s.Tank.Color);
        s.World.Machines.Add(harvester);
        s.World.PlantMachine(harvester, s.Base);
        int store = s.Base.Store.Ore;

        var events = StepUntil(s.World, e => e.Kind == StepEventKind.OreHarvested);

        var harvested = Assert.Single(events, e => e.Kind == StepEventKind.OreHarvested);
        Assert.Equal((s.Tank.Color, MachineType.Harvester, Tweaks.Ore.PlainCellUnits), (harvested.Color, harvested.Machine, harvested.Count));
        Assert.Equal(TerrainPixel.Blank, s.World.Terrain.GetPixelRaw(harvested.Position));
        Assert.Equal(store + harvested.Count, s.Base.Store.Ore);
    }

    [Fact]
    public void Recording_DoesNotAllocateOnceTheLogHasGrown()
    {
        var log = new StepEventLog();
        for (int i = 0; i < 64; i++)
            log.Record(new StepEvent(StepEventKind.Explosion, new Position(i, i)));
        log.Clear();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int round = 0; round < 10; round++)
        {
            for (int i = 0; i < 64; i++)
                log.Record(new StepEvent(StepEventKind.ShotFired, new Position(i, 0), 1));
            log.Clear();
        }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
