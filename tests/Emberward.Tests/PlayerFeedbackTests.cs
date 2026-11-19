using Emberward.Core.Config;
using Emberward.Core.Entities;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Input;
using Emberward.Core.Resources;
using Emberward.Core.Terrain;
using Emberward.Core.Types;
using Emberward.Core.Bots;
using Emberward.Core.Network;
using Emberward.Core.Entities.Tanks;

namespace Emberward.Tests;

/// <summary>
/// Rules that exist so the player understands what happens to their tank: damage causes and hit flashes,
/// respawn protection, refusing relay plants that would not link, and links seeing through base walls.
/// </summary>
public class PlayerFeedbackTests
{
    [Fact]
    public void EnemyHit_RecordsCauseFlashAndDirection()
    {
        var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false);
        var tank = world.TankList.Tanks[0];
        int before = tank.Reactor.Health;

        tank.TakeDamage(Tweaks.Weapon.BulletDamage, DamageCause.EnemyFire, new VectorF(3f, 0f));

        Assert.Equal(before - Tweaks.Weapon.BulletDamage, tank.Reactor.Health);
        Assert.Equal(DamageCause.EnemyFire, tank.LastDamageCause);
        Assert.Equal(Tank.HitFlashDuration, tank.HitFlashTicks);
        Assert.Equal(3f, tank.LastHitVelocity.X);
        Assert.Equal(DamageCause.None, tank.DeathCause);
    }

    [Fact]
    public void LethalDamage_RecordsDeathCause_AndRespawnClearsIt()
    {
        var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false);
        var tank = world.TankList.Tanks[0];

        tank.TakeDamage(tank.Reactor.Health + 10, DamageCause.Overheat);

        Assert.True(tank.IsDead);
        Assert.Equal(DamageCause.Overheat, tank.DeathCause);

        tank.Spawn(world.Terrain.Ledger);
        Assert.False(tank.IsDead);
        Assert.Equal(DamageCause.None, tank.DeathCause);
    }

    [Fact]
    public void Respawn_ProtectsFromEnemyFire_ButNotFromOverheating()
    {
        var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false);
        var tank = world.TankList.Tanks[0];
        tank.Spawn(world.Terrain.Ledger);
        int full = tank.Reactor.Health;

        tank.TakeDamage(Tweaks.Weapon.BulletDamage, DamageCause.EnemyFire);
        Assert.Equal(full, tank.Reactor.Health);

        tank.TakeDamage(5, DamageCause.Overheat);
        Assert.Equal(full - 5, tank.Reactor.Health);

        // The shield holds while the tank stays home, then expires shortly after it leaves.
        for (int i = 0; i < Tank.SpawnProtectionDuration * 2; i++)
            world.Advance(_ => default);
        Assert.True(tank.SpawnProtectionTicks > 0, "the shield holds while the tank is in its base");
        tank.Position = tank.Base!.Position + new Offset(0, Tweaks.Base.BaseSize);
        for (int i = 0; i <= Tank.SpawnProtectionDuration; i++)
            world.Advance(_ => default);
        Assert.Equal(0, tank.SpawnProtectionTicks);

        tank.TakeDamage(Tweaks.Weapon.BulletDamage, DamageCause.EnemyFire);
        Assert.True(tank.Reactor.Health < full - 5);
    }

    [Fact]
    public void RelayPlant_IsRefused_WhenTheRelayWouldNotLink()
    {
        var s = RelayHeatTests.CreateScenario(blockLineOfSight: true);
        s.Tank.Hold.Add(20);
        s.Tank.Position = s.RelayPos;

        Press(s.World, true);
        Press(s.World, false);
        Assert.True(s.Tank.IsCarryingMachine);
        int oreAfterBuild = s.Tank.OreHeld;

        Press(s.World, true);

        Assert.True(s.Tank.IsCarryingMachine, "a relay that would not link must stay in the tank");
        Assert.True(s.Tank.PlantRefusedTicks > 0);
        Assert.Equal(Emberward.Core.Entities.Tanks.BuildRefusal.WillNotLink, s.Tank.LastBuildRefusal);
        Assert.Equal(oreAfterBuild, s.Tank.OreHeld);
        Assert.Equal(LinkPreviewStatus.Blocked, s.World.LinkMap.PreviewLink(s.Tank.Position, s.World.Terrain).Status);
    }

    [Fact]
    public void LinkPreview_SeesThroughBaseWalls()
    {
        var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false);
        world.Advance(_ => default); // build the link graph so the bases are powered
        var basePos = world.TankList.Tanks[0].Base!.Position;

        // A clear corridor beside the base's side wall (not through the door), ending 12 cells outside it.
        int half = Tweaks.Base.BaseSize / 2;
        for (int dx = half + 1; dx <= half + 12; dx++)
        {
            foreach (int sign in new[] { -1, 1 })
            {
                var p = basePos + new Offset(sign * dx, 0);
                if (world.Terrain.IsInside(p))
                    world.Terrain.SetPixel(p, TerrainPixel.Blank);
            }
        }

        var right = basePos + new Offset(half + 12, 0);
        var left = basePos + new Offset(-(half + 12), 0);
        var target = world.Terrain.IsInside(right) ? right : left;
        var preview = world.LinkMap.PreviewLink(target, world.Terrain);

        Assert.Equal(LinkPreviewStatus.Live, preview.Status);
        Assert.Equal(basePos, preview.Target);
    }

    [Fact]
    public void BotTankAI_HoldsAimedFire_DuringTheOpening()
    {
        var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false);
        var bot = new BotTankAI(seed: 7);
        var self = world.TankList.Tanks[1];
        var enemy = world.TankList.Tanks[0];

        // Put the enemy in plain sight next to the bot: during the opening it must not fire at it.
        int shots = 0;
        for (int i = 0; i < 10 * Tweaks.Perf.TargetFps; i++)
        {
            enemy.Position = self.Position + new Offset(6, 0);
            var output = bot.GetInput(self, enemy, world);
            if (output.ShootPrimary && output.AimDirection.X > 0.9f)
                shots++;
        }

        Assert.Equal(0, shots);
    }

    [Fact]
    public void RespawnShield_EndsWhenTheTankFires()
    {
        var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false);
        var tank = world.TankList.Tanks[0];
        tank.Spawn(world.Terrain.Ledger);
        Assert.True(tank.SpawnProtectionTicks > 0);

        world.Advance(i => i == 0 ? new ControllerOutput { ShootPrimary = true } : default);

        Assert.Equal(0, tank.SpawnProtectionTicks);
    }

    [Fact]
    public void DockedTank_BuildsARelayFromTheBaseStore()
    {
        var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false);
        var tank = world.TankList.Tanks[0];
        int store = tank.Base!.Store.Ore;
        Assert.Equal(Tweaks.Ore.BaseStartingStore, store);
        Assert.Equal(0, tank.OreHeld);

        Press(world, true);

        Assert.True(tank.IsCarryingMachine);
        Assert.Equal(store - Tweaks.Ore.RelayCost, tank.Base.Store.Ore);
    }

    private static void Press(Emberward.Core.World world, bool pressed)
        => world.Advance(i => i == 0 ? new ControllerOutput { BuildPrimary = pressed } : default);
}
