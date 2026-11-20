using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Entities.Projectiles;
using Emberward.Core.Resources;
using Emberward.Core.Terrain;
using Emberward.Core.Types;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Entities.Bases;

namespace Emberward.Tests;

/// <summary>
/// Bullets hit planted machines: an enemy bullet takes health off the machine, a bullet from the machine's own side
/// passes through it, and a machine at zero health is wrecked (out of the network, battery dumped as booked heat,
/// scorch left on the ground).
/// </summary>
public class MachineDamageTests
{
    private static int EnemyColor(RelayHeatTests.Scenario s) =>
        s.World.TankList.Tanks.First(t => t.Color != s.Tank.Color).Color;

    private static Machine Plant(RelayHeatTests.Scenario s, MachineType type, int ownerColor)
    {
        var owner = s.World.TankBases.Bases.First(b => b.Color == ownerColor);
        var machine = new Machine(s.RelayPos, type, ownerColor);
        s.World.Machines.Add(machine);
        s.World.PlantMachine(machine, owner);
        return machine;
    }

    /// <summary>A bullet down the scenario tunnel toward the machine spot, a few cells short of it.</summary>
    private static Projectile FireAtRelaySpot(RelayHeatTests.Scenario s, int shooterColor)
    {
        var bullet = Projectile.CreateBullet(s.RelayPos + new Offset(0, -s.DirY * 8),
            new VectorF(0, s.DirY * Tweaks.Weapon.CannonBulletSpeed), shooterColor);
        s.World.Projectiles.Add(bullet);
        return bullet;
    }

    private static void AdvanceUntilDead(World world, Projectile bullet)
    {
        for (int i = 0; i < 60 && bullet.IsAlive; i++)
            world.Advance(_ => default);
        Assert.False(bullet.IsAlive, "the bullet should have hit something");
    }

    [Fact]
    public void EnemyBullet_DamagesMachineAndStopsOnIt()
    {
        var s = RelayHeatTests.CreateScenario();
        var relay = Plant(s, MachineType.Relay, EnemyColor(s));

        var bullet = FireAtRelaySpot(s, s.Tank.Color);
        AdvanceUntilDead(s.World, bullet);

        Assert.Equal(Tweaks.Machine.ReactorHealthCapacity - Tweaks.Weapon.BulletDamage, (int)relay.Reactor.Health);
        Assert.True(relay.IsAlive);
        Assert.True(relay.TestCollide((Position)bullet.Position), "the bullet stops on the machine");
    }

    [Fact]
    public void OwnBullet_PassesThroughOwnMachine()
    {
        var s = RelayHeatTests.CreateScenario();
        var relay = Plant(s, MachineType.Relay, s.Tank.Color);

        var bullet = FireAtRelaySpot(s, s.Tank.Color);
        AdvanceUntilDead(s.World, bullet);

        Assert.Equal(Tweaks.Machine.ReactorHealthCapacity, (int)relay.Reactor.Health);
        Assert.True(relay.IsAlive);
        int beyond = (((Position)bullet.Position).Y - relay.Position.Y) * s.DirY;
        Assert.True(beyond > Tweaks.Machine.BoundingBoxHalfSize, $"the bullet should fly on past its own machine (stopped {beyond} past it)");
    }

    [Fact]
    public void MachineAtZeroHealth_IsWreckedOutOfTheNetworkAndLeavesScorch()
    {
        var s = RelayHeatTests.CreateScenario();
        var relay = Plant(s, MachineType.Relay, EnemyColor(s));
        var point = relay.LinkPoint!;
        Assert.Contains(point, s.World.LinkMap.Points);
        relay.Reactor.Exhaust(new ReactorState(0, Tweaks.Machine.ReactorHealthCapacity - Tweaks.Weapon.BulletDamage));

        var bullet = FireAtRelaySpot(s, s.Tank.Color);
        AdvanceUntilDead(s.World, bullet);

        Assert.False(relay.IsAlive);
        Assert.DoesNotContain(relay, s.World.Machines.Machines);
        Assert.DoesNotContain(point, s.World.LinkMap.Points);
        Assert.DoesNotContain(s.World.LinkMap.Links, l => l.From == point || l.To == point);

        var terrain = s.World.Terrain;
        Assert.Equal(TerrainPixel.Decal, terrain.GetPixelRaw(relay.Position));
        int scorched = 0;
        int half = Tweaks.Machine.WreckScorchHalfSize;
        for (int dy = -half; dy <= half; dy++)
            for (int dx = -half; dx <= half; dx++)
            {
                var cell = relay.Position + new Offset(dx, dy);
                if (Pixel.IsScorched(terrain.GetPixelRaw(cell)) && terrain.GetScorchIntensity(cell.X + cell.Y * terrain.Width) > 0)
                    scorched++;
            }
        Assert.True(scorched >= 9, $"the machine's footprint should be scorched (got {scorched} cells)");
    }

    [Fact]
    public void WreckedCapacitor_DumpsItsChargeAsBookedHeat()
    {
        // Only the energy network gives a machine a capacitor to dump.
        var s = RelayHeatTests.CreateScenario(energyNetwork: true);
        var harvester = Plant(s, MachineType.Harvester, EnemyColor(s));
        Assert.True(harvester.Charge > 0.0, "planting fills the capacitor from the owner base");
        harvester.Reactor.Exhaust(new ReactorState(0, Tweaks.Machine.ReactorHealthCapacity - Tweaks.Weapon.BulletDamage));
        var ledger = s.World.Terrain.Ledger;

        double start = s.World.SumSystemThermalEnergy() + s.World.SumSystemCharge();
        double startExternal = ledger.Injected - ledger.TotalSunk;
        double maxResidual = 0.0;
        var bullet = FireAtRelaySpot(s, s.Tank.Color);
        for (int i = 0; i < 60; i++)
        {
            s.World.Advance(_ => default);
            double now = s.World.SumSystemThermalEnergy() + s.World.SumSystemCharge();
            double residual = (now - start) - (ledger.Injected - ledger.TotalSunk - startExternal);
            maxResidual = Math.Max(maxResidual, Math.Abs(residual));
        }

        Assert.False(bullet.IsAlive);
        Assert.False(harvester.IsAlive);
        Assert.DoesNotContain(harvester, s.World.Machines.Machines);
        Assert.Equal(0.0, harvester.Charge, 6);
        Assert.True(maxResidual < 1.0, $"heat and charge must add up through the wreck. residual={maxResidual:0.0000}");
    }

    [Fact]
    public void ShootingEnemyMachinesToPieces_IsDeterministic()
    {
        static ulong Run()
        {
            var s = RelayHeatTests.CreateScenario();
            Plant(s, MachineType.Relay, EnemyColor(s));
            for (int i = 0; i < 120; i++)
            {
                if (i % 6 == 0)
                    FireAtRelaySpot(s, s.Tank.Color);
                s.World.Advance(_ => default);
            }
            Assert.Empty(s.World.Machines.Machines);
            return WorldStateHash.Compute(s.World);
        }

        Assert.Equal(Run(), Run());
    }
}
