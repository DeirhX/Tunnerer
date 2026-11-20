namespace Emberward.Tests;

using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Entities.Bases;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Input;
using Emberward.Core.Match;
using Emberward.Core.Network;
using Emberward.Core.Terrain;
using Emberward.Core.Types;
using static Emberward.Tests.SnapshotRoundTrip;

public class GeneratorTests
{
    private static World BlankWorld()
    {
        var world = new World(new Size(128, 100), rules: new MatchRules { TerrainRegrowth = false, EnergyNetwork = true });
        world.Terrain.Fill(TerrainPixel.Blank);
        return world;
    }

    private static Machine Plant(World world, MachineType type, Position position, TankBase? owner = null)
    {
        var machine = new Machine(position, type, owner?.Color ?? 0);
        world.Machines.Add(machine);
        world.PlantMachine(machine, owner);
        return machine;
    }

    private static void Advance(World world, int ticks)
    {
        for (int i = 0; i < ticks; i++)
            world.Advance(_ => default);
    }

    private static void PutHeatStock(World world, Position generator)
    {
        int radius = Tweaks.Machine.GeneratorIntakeRadius;
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = radius - 1; dx <= radius; dx++)
                world.Terrain.SetPixel(generator + new Offset(dx, dy), TerrainPixel.Dirt);
        world.Terrain.PlaceHotRockPocket(generator + new Offset(radius - 1, 0), 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FourthKey_BuildsAndPlantsOnlyInEnergyMatches(bool energyNetwork)
    {
        var s = RelayHeatTests.CreateScenario(blockLineOfSight: true, energyNetwork: energyNetwork);
        s.Tank.Hold.Ore = Tweaks.Ore.GeneratorCost;
        var press = new ControllerOutput { BuildQuaternary = true };
        s.Tank.Position = s.RelayPos;
        s.World.Advance(i => i == 0 ? press : default);
        Assert.Equal(energyNetwork ? MachineType.Generator : (MachineType?)null, s.Tank.CarriedMachineType);

        Advance(s.World, 1);
        s.World.Advance(i => i == 0 ? press : default);
        Assert.Null(s.Tank.CarriedMachineType);
        if (energyNetwork)
        {
            var generator = Assert.Single(s.World.Machines.Machines);
            Assert.True(generator.IsStanding);
            Assert.False(generator.LinkPoint!.IsPowered);
        }
        else
        {
            Assert.Empty(s.World.Machines.Machines);
            Assert.Equal(Tweaks.Ore.GeneratorCost, s.Tank.Hold.Ore);
        }
    }

    [Fact]
    public void DisconnectedIsland_ProvidesCharge_AndSurvivesASnapshot()
    {
        var s = RelayHeatTests.CreateScenario(blockLineOfSight: true, energyNetwork: true);
        var generator = Plant(s.World, MachineType.Generator, s.RelayPos, s.Base);
        var relayAt = s.RelayPos + new Offset(0, 6 * s.DirY);
        var relay = Plant(s.World, MachineType.Relay, relayAt, s.Base);
        relay.Charge -= 200.0;
        PutHeatStock(s.World, s.RelayPos);
        double home = s.Base.Charge;
        double before = relay.Charge;
        Advance(s.World, 32);

        Assert.False(relay.LinkPoint!.IsPowered);
        Assert.True(s.World.MachinePowerOf(relay)!.Value.IsFed);
        Assert.True(relay.Charge > before);
        Assert.True(s.World.Energy!.StoredCharge() > 350.0);
        Assert.Equal(home, s.Base.Charge);
        Assert.Equal(0.0, s.World.BasePowerOf(s.Base)!.Value.GeneratorProductionPerSecond);
        Assert.True(s.World.MachinePowerOf(generator)!.Value.ProductionPerSecond > 0.0);

        var preview = s.World.PreviewPlacement(MachineType.Relay, relayAt + new Offset(0, 4 * s.DirY), s.Base);
        Assert.Equal(LinkPreviewStatus.Live, preview.Link.Status);
        AssertPlaysOnAlike(s.World, [null, null],
            () => RelayHeatTests.CreateScenario(blockLineOfSight: true, energyNetwork: true).World,
            ticks: 24, (_, _) => default);
    }

    [Fact]
    public void AHarvesterCanRechargeFromAnIslandWithoutAHomeLink()
    {
        var s = RelayHeatTests.CreateScenario(blockLineOfSight: true, energyNetwork: true);
        var generator = Plant(s.World, MachineType.Generator, s.RelayPos, s.Base);
        var harvester = Plant(s.World, MachineType.Harvester, s.RelayPos + new Offset(0, 6 * s.DirY), s.Base);
        harvester.Charge -= 180.0;
        double before = harvester.Charge;
        PutHeatStock(s.World, generator.Position);
        Advance(s.World, 35);
        Assert.True(harvester.IsOffNetwork);
        Assert.True(s.World.MachinePowerOf(harvester)!.Value.IsFed);
        Assert.True(harvester.Charge > before);
    }

    [Fact]
    public void ALocalIslandSplitsOnAClosedSightline()
    {
        var world = BlankWorld();
        var generator = Plant(world, MachineType.Generator, new Position(36, 35));
        var relay = Plant(world, MachineType.Relay, new Position(36, 52));
        PutHeatStock(world, generator.Position);
        generator.Charge = generator.ChargeCapacity;
        relay.Charge = relay.ChargeCapacity;
        Advance(world, 24);
        Assert.True(world.MachinePowerOf(relay)!.Value.IsFed);

        world.Terrain.SetPixel(new Position(36, 45), TerrainPixel.Rock);
        Advance(world, 12);
        Assert.False(world.MachinePowerOf(relay)!.Value.IsFed);
        Assert.True(world.MachinePowerOf(generator)!.Value.IsFed);

        world.Terrain.SetPixel(new Position(36, 45), TerrainPixel.Blank);
        Advance(world, 12);
        Assert.True(world.MachinePowerOf(relay)!.Value.IsFed);
    }

    [Fact]
    public void AGeneratorOnTheHomeNetwork_ContributesToTheBaseRowAndStore()
    {
        var s = RelayHeatTests.CreateScenario(energyNetwork: true);
        var generator = Plant(s.World, MachineType.Generator, s.RelayPos, s.Base);
        PutHeatStock(s.World, generator.Position);
        double stored = s.Base.Charge;
        Advance(s.World, 48);

        Assert.True(generator.LinkPoint!.IsPowered);
        Assert.True(s.Base.Charge > stored);
        var power = s.World.BasePowerOf(s.Base)!.Value;
        Assert.True(power.GeneratorProductionPerSecond > 0.0);
        Assert.Equal(power.GeneratorProductionPerSecond, power.ProductionPerSecond);
    }

    [Fact]
    public void ClosedLoopOnAmbientGround_NeverGainsCharge()
    {
        var world = BlankWorld();
        var generator = Plant(world, MachineType.Generator, new Position(36, 34));
        var relay = Plant(world, MachineType.Relay, new Position(40, 34));
        generator.Charge = generator.ChargeCapacity;
        relay.Charge = relay.ChargeCapacity;
        double initial = world.SumSystemCharge();
        for (int i = 0; i < 240; i++)
        {
            world.Advance(_ => default);
            Assert.True(world.SumSystemCharge() <= initial);
        }
        Assert.True(world.SumSystemCharge() < initial);
    }

    [Fact]
    public void TorchingBesideTheGenerator_WakesItWithoutExceedingTheCap()
    {
        var s = RelayHeatTests.CreateScenario(blockLineOfSight: true, energyNetwork: true);
        var generator = Plant(s.World, MachineType.Generator, s.RelayPos, s.Base);
        s.Tank.Position = s.RelayPos;
        Advance(s.World, 5);
        Assert.Equal(0.0, generator.LastChargeMade);
        var torch = new ControllerOutput
        {
            MoveSpeed = new Offset(1, 0),
            ShootPrimary = true,
            AimDirection = new DirectionF(1, 0),
        };
        var ledger = s.World.Terrain.Ledger;
        double injected = ledger.Injected, converted = ledger.ConvertedToCharge;
        const int ticks = 48;
        for (int i = 0; i < ticks; i++)
        {
            s.World.Advance(n => n == 0 ? torch : default);
            Assert.InRange(generator.LastChargeMade, 0.0, Tweaks.Machine.GeneratorMaxChargePerTick);
        }
        Assert.True(ledger.Injected > injected);
        Assert.InRange(ledger.ConvertedToCharge - converted, 1.0, ticks * Tweaks.Machine.GeneratorMaxChargePerTick);
    }

    [Fact]
    public void FreshHotRockCanReachTheOutputCap()
    {
        var world = BlankWorld();
        var generator = Plant(world, MachineType.Generator, new Position(36, 35));
        PutHeatStock(world, generator.Position);
        Advance(world, 1);
        Assert.Equal(Tweaks.Machine.GeneratorMaxChargePerTick, generator.LastChargeMade, tolerance: 0.001);
    }

    [Fact]
    public void PackedGenerators_MakeLessChargeThanSpacedGenerators()
    {
        static double Production(int spacing)
        {
            var world = BlankWorld();
            var first = Plant(world, MachineType.Generator, new Position(45, 28));
            var second = Plant(world, MachineType.Generator, first.Position + new Offset(0, spacing));
            PutHeatStock(world, first.Position);
            PutHeatStock(world, second.Position);
            Advance(world, 55);
            return world.Terrain.Ledger.ConvertedToCharge;
        }

        double packed = Production(5), spaced = Production(24);
        Assert.True(packed < spaced, $"Packed made {packed:0.0} charge and spaced made {spaced:0.0}.");
    }

    [Fact]
    public void HeatPlusCharge_StaysBalancedWhenAGeneratorIsWrecked()
    {
        var world = BlankWorld();
        var generator = Plant(world, MachineType.Generator, new Position(36, 35));
        PutHeatStock(world, generator.Position);
        double openingCharge = generator.Charge = generator.ChargeCapacity;
        var ledger = world.Terrain.Ledger;
        double start = world.SumSystemThermalEnergy() + world.SumSystemCharge() - ledger.Injected + ledger.TotalSunk;

        for (int i = 0; i < 85; i++)
        {
            if (i == 45)
            {
                Assert.True(world.Energy!.StoredCharge() > 400.0);
                Assert.True(generator.TakeDamage(Tweaks.Machine.ReactorHealthCapacity));
            }

            world.Advance(_ => default);
            Assert.Equal(start, world.SumSystemThermalEnergy() + world.SumSystemCharge() - ledger.Injected + ledger.TotalSunk, tolerance: 0.2);
        }
        Assert.Equal(-openingCharge, ledger.ConvertedToCharge, tolerance: 1e-8);
        Assert.Equal(0.0, world.SumSystemCharge());
    }
}
