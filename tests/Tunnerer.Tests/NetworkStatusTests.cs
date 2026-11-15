using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Types;

namespace Tunnerer.Tests;

public class NetworkStatusTests
{
    private static (World World, TankBase Own, TankBase Other) CreateWorld()
    {
        var world = TestHelpers.CreateSeededWorld(new Size(320, 200));
        return (world, world.TankBases.GetSpawn(0)!, world.TankBases.GetSpawn(1)!);
    }

    private static Machine Plant(World world, MachineType type, TankBase owner)
    {
        var machine = new Machine(owner.Position + new Offset(0, 30), type, owner.Color);
        world.Machines.Add(machine);
        world.PlantMachine(machine, owner);
        return machine;
    }

    [Fact]
    public void EmptyNetwork_HasNoMachines_AndReportsTheBaseRadiator()
    {
        var (world, own, _) = CreateWorld();
        var status = world.GetNetworkStatus(own);

        Assert.Equal(0, status.Harvesters);
        Assert.Equal(0, status.Relays);
        Assert.Equal(0.0, status.RelayInflow);
        Assert.Equal(Tweaks.Base.HeatCooldown * (double)Tweaks.Base.ReactorEnergyPerHeatUnit, status.VentCapacity);
        Assert.Equal(own.HeatStoreFill, status.HeatStoreFill);
        Assert.InRange(status.HeatStoreFill, 0.0, 1.0);
    }

    [Fact]
    public void Harvesters_AreCountedInExactlyOneState()
    {
        var (world, own, _) = CreateWorld();
        var working = Plant(world, MachineType.Harvester, own);
        var returning = Plant(world, MachineType.Harvester, own);
        returning.IsReturning = true;
        returning.Charge = 0.0; // also flat, but returning comes first
        var flat = Plant(world, MachineType.Harvester, own);
        flat.Charge = Tweaks.Machine.HarvesterHeatPerHarvest - 1.0;
        flat.IsStalled = true; // also hot, but out of charge comes first
        var hot = Plant(world, MachineType.Harvester, own);
        hot.IsStalled = true;
        // Planting fills batteries from the base store, which may not cover all four.
        working.Charge = hot.Charge = working.ChargeCapacity;

        var status = world.GetNetworkStatus(own);

        Assert.Equal(HarvesterState.Working, MachineBehaviors.HarvesterStateOf(working));
        Assert.Equal(4, status.Harvesters);
        Assert.Equal(1, status.HarvestersReturning);
        Assert.Equal(1, status.HarvestersOutOfCharge);
        Assert.Equal(1, status.HarvestersTooHot);
        Assert.Equal(1, status.HarvestersWorking);
    }

    [Fact]
    public void Relays_ReportInflow_PumpingAndRadiatorShare()
    {
        var (world, own, _) = CreateWorld();
        var idle = Plant(world, MachineType.Relay, own);
        idle.LastPumpedEnergy = 0.5;
        var pumping = Plant(world, MachineType.Relay, own);
        pumping.LastPumpedEnergy = 40.0;

        var status = world.GetNetworkStatus(own);

        Assert.Equal(2, status.Relays);
        Assert.Equal(1, status.PumpingRelays);
        Assert.Equal(40.5, status.RelayInflow);
        Assert.Equal(40.5 - own.EngineOutputPerTick, status.RadiatorInflow);
        int powered = (idle.LinkPoint?.IsPowered == true ? 1 : 0) + (pumping.LinkPoint?.IsPowered == true ? 1 : 0);
        Assert.Equal(powered, status.PoweredRelays);
    }

    [Fact]
    public void OnlyLivePlantedMachinesOfTheBaseCount()
    {
        var (world, own, other) = CreateWorld();
        Plant(world, MachineType.Relay, own);
        Plant(world, MachineType.Relay, other);
        Plant(world, MachineType.Harvester, own).IsAlive = false;
        Plant(world, MachineType.Pylon, own);
        world.Machines.Add(new Machine(own.Position, MachineType.Relay, own.Color) { OwnerBase = own });

        var status = world.GetNetworkStatus(own);

        Assert.Equal(1, status.Relays);
        Assert.Equal(0, status.Harvesters);
    }

    [Fact]
    public void OffNetworkSecondsToOverheat_CountsDownToZero()
    {
        var (world, _, _) = CreateWorld();
        var tank = world.TankList.Tanks[0];

        Assert.Equal(Tweaks.Tank.HeatSafeMax / Tweaks.Tank.OffNetworkHeatPerSecond, tank.OffNetworkSecondsToOverheat);
    }
}
