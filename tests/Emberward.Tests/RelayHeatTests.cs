using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Input;
using Emberward.Core.Resources;
using Emberward.Core.Terrain;
using Emberward.Core.Types;
using Emberward.Core.Network;
using Emberward.Core.Entities.Bases;
using static Emberward.Tests.TankHeatTestHelpers;

namespace Emberward.Tests;

/// <summary>
/// Relays are heat pipes: planted in line of sight of the base they pull heat out of their surroundings and store
/// it in the base reactor, which is a finite reservoir that vents at a fixed radiator rate.
/// </summary>
public class RelayHeatTests
{
    internal sealed record Scenario(World World, Tank Tank, TankBase Base, Position RelayPos, int DirY);

    /// <summary>
    /// A base with a straight 5-wide tunnel leaving through its door toward the farther map edge. The relay
    /// position is 30 cells beyond the door, well inside the live link range.
    /// </summary>
    internal static Scenario CreateScenario(int seed = 4242, bool blockLineOfSight = false, bool energyNetwork = false,
        bool machineUpgrades = false, bool tankReserve = false, bool mobs = false)
    {
        var world = TestHelpers.CreateSeededWorld(seed: seed, enableTerrainRegrowth: false, energyNetwork: energyNetwork,
            machineUpgrades: machineUpgrades, tankReserve: tankReserve, mobs: mobs);
        var tank = world.TankList.Tanks[0];
        var tankBase = tank.Base!;
        var basePos = tankBase.Position;

        int dirY = (world.Terrain.Height - basePos.Y) >= basePos.Y ? 1 : -1;
        int firstOutside = Tweaks.Base.BaseSize / 2 + 1;
        for (int dx = -2; dx <= 2; dx++)
            CarveVerticalLine(world.Terrain, basePos + new Offset(dx, 0), dirY * firstOutside, dirY * (firstOutside + 50));

        if (blockLineOfSight)
        {
            for (int dx = -3; dx <= 3; dx++)
                world.Terrain.SetPixel(basePos + new Offset(dx, dirY * (firstOutside + 15)), TerrainPixel.Rock);
        }

        var relayPos = basePos + new Offset(0, dirY * (firstOutside + 30));
        return new Scenario(world, tank, tankBase, relayPos, dirY);
    }

    internal static Machine PlantRelay(Scenario s, Position? at = null)
    {
        var relay = new Machine(at ?? s.RelayPos, MachineType.Relay, s.Tank.Color);
        s.World.Machines.Add(relay);
        s.World.PlantMachine(relay, s.Base);
        return relay;
    }

    private static void HeatZone(Scenario s, int amount = 120, int radius = 6)
        => s.World.Terrain.AddHeatRadius(s.RelayPos + new Offset(0, s.DirY * 2), amount, radius);

    private static float ZoneHeat(Scenario s)
        => s.World.Terrain.SampleAverageHeat(s.RelayPos + new Offset(0, s.DirY * 2), radius: 6) * 255f;

    private static void Advance(World world, int ticks)
    {
        for (int i = 0; i < ticks; i++)
            world.Advance(_ => default);
    }

    [Fact]
    public void Relay_WithLineOfSight_PumpsHeatIntoBaseReactor()
    {
        var withRelay = CreateScenario();
        var control = CreateScenario();
        HeatZone(withRelay);
        HeatZone(control);
        var relay = PlantRelay(withRelay);

        double pumped = 0.0;
        for (int i = 0; i < 240; i++)
        {
            withRelay.World.Advance(_ => default);
            control.World.Advance(_ => default);
            pumped += relay.LastPumpedEnergy;
        }

        Assert.True(relay.LinkPoint!.IsPowered, "the relay should be connected to the base through a live link");
        Assert.True(pumped > 1000.0, $"the relay should move real heat into the base. pumped={pumped:0.0}");
        Assert.True(ZoneHeat(withRelay) < ZoneHeat(control) * 0.6f,
            $"the relay should clear the hot zone much faster. withRelay={ZoneHeat(withRelay):0.0}, control={ZoneHeat(control):0.0}");
    }

    [Fact]
    public void ARelayInColdGround_PumpsNothing_AndStartsAgainOnceHeatReachesIt()
    {
        // A relay that finds no heat stops sweeping its ground until heat moves there. Whether it sleeps cannot be
        // seen from outside; that it wakes can: heat written beside it, or spreading in from further off, is pumped.
        var s = CreateScenario();
        var relay = PlantRelay(s);
        Advance(s.World, 60);
        Assert.True(relay.LinkPoint!.IsPowered);
        Assert.Equal(0.0, relay.LastPumpedEnergy);

        HeatZone(s);
        Advance(s.World, 1);
        Assert.True(relay.LastPumpedEnergy > 0.0, "heat written into its reach is pumped on the next tick");

        // Left alone the zone is pumped out and the relay rests again.
        Advance(s.World, 3000);
        Assert.Equal(0.0, relay.LastPumpedEnergy);

        // Heat outside its reach that spreads into it wakes it as well.
        int beyond = Tweaks.Machine.RelayCoolingRadius + 3;
        s.World.Terrain.AddHeatRadius(s.RelayPos + new Offset(0, s.DirY * beyond), 200, 2);
        double pumped = 0.0;
        for (int i = 0; i < 600; i++)
        {
            s.World.Advance(_ => default);
            pumped += relay.LastPumpedEnergy;
        }
        Assert.True(pumped > 0.0, "heat that spreads into its reach is pumped");
    }

    [Fact]
    public void TheGroundsQuietStamp_HoldsWhileNoHeatMoves_AndChangesWhenItDoes()
    {
        var terrain = new TerrainGrid(new Size(200, 200));
        terrain.Fill(TerrainPixel.Dirt);
        var at = new Position(100, 100);
        for (int i = 0; i < 5; i++)
            terrain.CoolDown();
        long quiet = terrain.QuietHeatStamp(at, 12);
        Assert.True(quiet >= 0);
        for (int i = 0; i < 20; i++)
            terrain.CoolDown();
        Assert.Equal(quiet, terrain.QuietHeatStamp(at, 12));

        // Heat written in reach: no promise while it moves, and another number once it has settled.
        terrain.AddHeat(at + new Offset(5, 0), 80);
        Assert.True(terrain.QuietHeatStamp(at, 12) < 0);
        for (int i = 0; i < 20000 && terrain.QuietHeatStamp(at, 12) < 0; i++)
            terrain.CoolDown();
        long settled = terrain.QuietHeatStamp(at, 12);
        Assert.True(settled >= 0 && settled != quiet, $"settled={settled} quiet={quiet}");

        // Heat far away leaves it as it is.
        terrain.AddHeat(new Position(20, 20), 80);
        for (int i = 0; i < 50; i++)
            terrain.CoolDown();
        Assert.Equal(settled, terrain.QuietHeatStamp(at, 12));
    }

    [Fact]
    public void Relay_WithoutLineOfSight_IsNotPoweredAndDoesNothing()
    {
        var s = CreateScenario(blockLineOfSight: true);
        var control = CreateScenario(blockLineOfSight: true); // identical world, no relay
        HeatZone(s);
        HeatZone(control);
        var relay = PlantRelay(s);

        double pumped = 0.0;
        for (int i = 0; i < 120; i++)
        {
            s.World.Advance(_ => default);
            control.World.Advance(_ => default);
            pumped += relay.LastPumpedEnergy;
        }

        Assert.False(relay.LinkPoint!.IsPowered, "a wall between relay and base must cut the link");
        Assert.Equal(0.0, pumped);
        Assert.Equal(ZoneHeat(control), ZoneHeat(s), precision: 3); // an unpowered relay changes nothing
    }

    [Fact]
    public void Relays_PumpingRate_IsLimitedByTheBaseRadiator()
    {
        // Four relays could pump 4 x RelayHeatPumpPerTick, more than the base vents per tick. With a full reactor
        // and unlimited supply, the sustained pump rate must settle at the radiator rate.
        var s = CreateScenario();
        var relays = new List<Machine>();
        for (int i = 0; i < 4; i++)
            relays.Add(PlantRelay(s, s.RelayPos + new Offset(0, s.DirY * (i - 1))));

        double ventPerTick = Tweaks.Base.HeatCooldown * (double)Tweaks.Base.ReactorEnergyPerHeatUnit;
        Assert.True(relays.Count * Tweaks.Machine.RelayHeatPumpPerTick > ventPerTick, "scenario must oversubscribe the radiator");

        s.Base.Reactor.Current.Heat = s.Base.Reactor.HeatCapacity; // reactor starts full

        const int ticks = 300;
        double pumped = 0.0;
        double chargeBefore = s.Base.Charge;
        for (int i = 0; i < ticks; i++)
        {
            HeatZone(s, amount: 255, radius: 12); // endless supply
            s.World.Advance(_ => default);
            foreach (var relay in relays)
                pumped += relay.LastPumpedEnergy;
        }

        // The engine turns part of the heat into charge; only the rest has to leave through the radiator.
        double charged = s.Base.Charge - chargeBefore;
        double heatShare = pumped - charged;
        Assert.True(charged > 0.0, "hot ground should make charge");
        Assert.True(heatShare <= ticks * ventPerTick + 1.0,
            $"the heat share cannot outrun the radiator. heat={heatShare:0.0}, limit={ticks * ventPerTick:0.0}");
        Assert.True(heatShare > ticks * ventPerTick * 0.8,
            $"the relays should keep the radiator busy. heat={heatShare:0.0}");
        Assert.True(pumped > ticks * ventPerTick * 1.2, $"the engine should let the relays move more heat than the radiator alone. pumped={pumped:0.0}");
    }

    [Fact]
    public void Relay_EnergyBalance_HoldsWithLedger()
    {
        var s = CreateScenario();
        HeatZone(s, amount: 200, radius: 8);
        PlantRelay(s);

        double start = s.World.SumSystemThermalEnergy();
        double startNet = s.World.Terrain.Ledger.NetExternal;
        double maxResidual = 0.0;
        for (int i = 0; i < 300; i++)
        {
            s.World.Advance(_ => default);
            double residual = (s.World.SumSystemThermalEnergy() - start) - (s.World.Terrain.Ledger.NetExternal - startNet);
            maxResidual = Math.Max(maxResidual, Math.Abs(residual));
        }

        Assert.True(maxResidual < 0.5, $"relay pumping is a transfer and must not create or lose energy. residual={maxResidual:0.0000}");
    }

    [Fact]
    public void Bases_AreRegisteredInTheLinkNetwork()
    {
        var world = TestHelpers.CreateSeededWorld(seed: 4242);

        int basePoints = world.LinkMap.Points.Count(p => p.Type == LinkPointType.Base);

        Assert.Equal(world.TankBases.Bases.Count, basePoints);
        Assert.All(world.TankBases.Bases, b => Assert.NotNull(b.LinkPoint));
    }

    [Fact]
    public void RemovedRelay_LeavesTheLinkNetwork()
    {
        var s = CreateScenario();
        var relay = PlantRelay(s);
        Advance(s.World, 10);
        int withRelay = s.World.LinkMap.PointCount;

        relay.IsAlive = false;
        Advance(s.World, 20); // machine list drops it, link map prunes on its next refresh

        Assert.Equal(0, s.World.Machines.Count);
        Assert.Equal(withRelay - 1, s.World.LinkMap.PointCount);
    }

    // ---- building ----

    private static ControllerOutput Build(bool pressed) => new() { BuildPrimary = pressed };

    private static void Press(World world, bool pressed)
        => world.Advance(i => i == 0 ? Build(pressed) : default);

    /// <summary>The tank out of its network's coverage (so its hold pays), with <paramref name="ore"/> in the hold.</summary>
    private static Scenario ScenarioWithTankOutsideBase(int ore)
    {
        var s = CreateScenario();
        s.Tank.Hold.Add(ore);
        s.Tank.Position = s.RelayPos; // far outside the base
        return s;
    }

    [Fact]
    public void BuildKey_BuildsCarriesAndPlantsARelay()
    {
        var s = ScenarioWithTankOutsideBase(ore: 20);
        var world = s.World;
        int holdBefore = s.Tank.OreHeld, storeBefore = s.Base.Store.Ore;

        Press(world, true);
        Assert.True(s.Tank.IsCarryingMachine);
        Assert.Equal(1, world.Machines.Count);
        var relay = world.Machines.Machines[0];
        Assert.Equal(MachineType.Relay, relay.Type);
        Assert.Equal(MachineState.Transporting, relay.State);
        Assert.Equal(holdBefore - Tweaks.Ore.RelayCost, s.Tank.OreHeld);
        Assert.Equal(storeBefore, s.Base.Store.Ore);

        // Holding the key does not plant; only a new press does. The relay follows the tank meanwhile.
        Press(world, true);
        Assert.True(s.Tank.IsCarryingMachine);
        Press(world, false);
        s.Tank.Position += new Offset(0, s.DirY * -5);
        Press(world, false);
        Assert.Equal(s.Tank.Position, relay.Position);

        int pointsBefore = world.LinkMap.PointCount;
        Press(world, true);

        Assert.False(s.Tank.IsCarryingMachine);
        Assert.Equal(MachineState.Planted, relay.State);
        Assert.Same(s.Base, relay.OwnerBase);
        Assert.NotNull(relay.LinkPoint);
        Assert.Equal(pointsBefore + 1, world.LinkMap.PointCount);
    }

    [Fact]
    public void BuildKey_DoesNothing_WithoutEnoughOre_AndSaysWhy()
    {
        // The base store could pay, but the tank is outside its network, so only the hold counts.
        var s = ScenarioWithTankOutsideBase(ore: Tweaks.Ore.RelayCost - 1);
        Assert.True(s.Base.Store.Ore >= Tweaks.Ore.RelayCost);

        Press(s.World, true);

        Assert.False(s.Tank.IsCarryingMachine);
        Assert.Equal(0, s.World.Machines.Count);
        Assert.Equal(BuildRefusal.NotEnoughOre, s.Tank.LastBuildRefusal);
        Assert.True(s.Tank.BuildRefusedTicks > 0);
        Assert.Equal(0, s.Tank.PlantRefusedTicks);
        Assert.Contains(s.World.StepEvents.ToArray(), e => e.Kind == StepEventKind.BuildRefused && e.Machine == MachineType.Relay);
    }

    [Fact]
    public void BuildKey_CannotPlantInsideABase()
    {
        var s = ScenarioWithTankOutsideBase(ore: 20);
        Press(s.World, true);
        Press(s.World, false);
        Assert.True(s.Tank.IsCarryingMachine);

        s.Tank.Position = s.Base.Position; // back home
        Press(s.World, true);

        Assert.True(s.Tank.IsCarryingMachine, "planting inside a base must be refused");
        Assert.Equal(MachineState.Transporting, s.World.Machines.Machines[0].State);
    }

    [Fact]
    public void CarriedRelay_IsLost_WhenTheTankDies()
    {
        var s = ScenarioWithTankOutsideBase(ore: 20);
        Press(s.World, true);
        Assert.Equal(1, s.World.Machines.Count);

        s.Tank.Reactor.Exhaust(new ReactorState(0, s.Tank.Reactor.Health)); // kill it
        Advance(s.World, 3);

        Assert.False(s.Tank.IsCarryingMachine);
        Assert.Equal(0, s.World.Machines.Count);
    }
}
