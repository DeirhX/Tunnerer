using Emberward.Core.Config;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Terrain;
using Emberward.Core.Types;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Entities.Bases;

namespace Emberward.Tests;

/// <summary>
/// The power loop (DESIGN.md §4.5): relay heat runs through the base engine, which turns a share of it into charge;
/// pylons hand that charge to harvester batteries, and the work they do turns it back into heat in the ground.
/// </summary>
public class PowerLoopTests
{
    private static Position PylonSpot(RelayHeatTests.Scenario s) =>
        s.Base.Position + new Offset(0, s.DirY * (Tweaks.Base.BaseSize / 2 + 4));

    private static Machine Plant(RelayHeatTests.Scenario s, MachineType type, Position at)
    {
        var machine = new Machine(at, type, s.Tank.Color);
        s.World.Machines.Add(machine);
        s.World.PlantMachine(machine, s.Base);
        return machine;
    }

    private static void Advance(RelayHeatTests.Scenario s, int ticks)
    {
        for (int i = 0; i < ticks; i++)
            s.World.Advance(_ => default);
    }

    [Fact]
    public void EngineEfficiency_RisesWithSourceTemperature_AndIsCapped()
    {
        var tankBase = new TankBase(new Position(0, 0), 0);
        Assert.Equal(0.0, tankBase.EngineEfficiency(tankBase.ReactorTemperature));
        Assert.Equal(0.0, tankBase.EngineEfficiency(-20));
        double warm = tankBase.EngineEfficiency(30), hot = tankBase.EngineEfficiency(100), extreme = tankBase.EngineEfficiency(10_000);
        Assert.True(warm > 0.0 && warm < hot, $"warm={warm}, hot={hot}");
        Assert.Equal(Tweaks.Base.EngineMaxEfficiency, extreme, precision: 6);
    }

    [Fact]
    public void EngineEfficiency_FallsAsTheReactorWarms()
    {
        var home = new TankBase(new Position(0, 0), 0);
        double startingEfficiency = home.EngineEfficiency(30);
        double startingTemperature = home.ReactorTemperature;

        home.AcceptTankHeat(home.ReactorRoomEnergy / 2);

        Assert.Equal(startingTemperature + Tweaks.Base.EngineColdSideRise / 2.0, home.ReactorTemperature);
        Assert.True(home.EngineEfficiency(30) < startingEfficiency);
        Assert.Equal(0.0, home.EngineEfficiency(home.ReactorTemperature));
    }

    [Fact]
    public void BaseCenter_CoolsOnlyByTheHeatTransferredAndVented()
    {
        var t = new TerrainGrid(new Size(48, 48));
        var home = new TankBase(new Position(24, 24), 0);
        t.SetPixel(home.Position, TerrainPixel.BaseCore);
        t.AddHeat(home.Position, 255);
        double injected = t.SumTotalThermalEnergy();

        home.Advance(t);

        Assert.InRange(t.GetHeatTemperature(home.Position), 0.1f, 254.9f);
        Assert.Equal(0.0, home.ReactorHeatEnergy); // fractional heat was vented too
        Assert.InRange(t.Ledger.SunkBase, 0.1, home.VentCapacityPerTick);
        Assert.Equal(injected - t.Ledger.SunkBase, t.SumTotalThermalEnergy(), precision: 4);
    }

    [Theory]
    [InlineData(0, 90, 200.0)]
    [InlineData(10, 90, 50.0)]
    [InlineData(80, 10, 50.0)]
    public void RelayConvertsEachFieldAtItsOwnTemperature(int rockHeat, int airHeat, double requested)
    {
        var t = new TerrainGrid(new Size(5, 5));
        var pos = new Position(2, 2);
        t.Fill(TerrainPixel.Rock);
        t.AddHeat(pos, rockHeat);
        t.AddAirHeatTotalInRadiusArea(pos, 0, airHeat);
        var home = new TankBase(pos, 0);
        double rockEnergy = Math.Min(requested, rockHeat * t.SumHeatCapacityInRadiusArea(pos, 0, TerrainGrid.ThermalField.Terrain));
        double airEnergy = Math.Min(requested - rockEnergy, airHeat * t.SumHeatCapacityInRadiusArea(pos, 0, TerrainGrid.ThermalField.Air));
        double rockCharge = rockEnergy * home.EngineEfficiency(rockHeat);
        var afterRock = new TankBase(pos, 0);
        afterRock.AcceptTankHeat(rockEnergy - rockCharge);
        double expectedCharge = rockCharge + airEnergy * afterRock.EngineEfficiency(airHeat);
        double initialCharge = home.Charge;
        double initialHeat = home.ReactorHeatEnergy;
        double initialGround = t.SumTotalThermalEnergy();

        double taken = home.AbsorbHeatFrom(t, pos, 0, requested, out bool limited, out double output, out bool cold);

        Assert.False(limited);
        Assert.False(cold);
        Assert.Equal(rockEnergy + airEnergy, taken, precision: 4);
        Assert.Equal(taken, initialGround - t.SumTotalThermalEnergy(), precision: 4);
        Assert.Equal(expectedCharge, output, precision: 4);
        Assert.Equal(expectedCharge, t.Ledger.ConvertedToCharge, precision: 4);
        Assert.Equal(expectedCharge, home.Charge - initialCharge, precision: 4);
        Assert.Equal(taken - expectedCharge, home.ReactorHeatEnergy - initialHeat, precision: 4);
    }

    [Fact]
    public void RelayLimitsItsAirDrawToTheRoomLeftAfterRock()
    {
        var t = new TerrainGrid(new Size(5, 5));
        var pos = new Position(2, 2);
        t.Fill(TerrainPixel.Rock);
        t.AddHeat(pos, 5);
        t.AddAirHeatTotalInRadiusArea(pos, 0, 100);
        var home = new TankBase(pos, 0);
        home.AcceptTankHeat(home.ReactorRoomEnergy - 20);

        double taken = home.AbsorbHeatFrom(t, pos, 0, 100, out bool limited, out double output, out bool cold);

        Assert.True(limited);
        Assert.False(cold);
        Assert.Equal(0f, t.GetHeatTemperature(pos), precision: 4);
        Assert.InRange(t.GetAirTemperature(pos), 1f, 99f);
        Assert.Equal(0, home.ReactorRoomEnergy, precision: 4);
        Assert.Equal(taken - 20, output, precision: 4);
        Assert.Equal(output, t.Ledger.ConvertedToCharge, precision: 4);
    }

    [Fact]
    public void RelayIntoAFullChargeStore_NeverDrainsTheStore()
    {
        var t = new TerrainGrid(new Size(5, 5));
        var pos = new Position(2, 2);
        t.Fill(TerrainPixel.Rock);
        var home = new TankBase(pos, 0);
        for (int i = 0; i < 20000 && home.Charge < home.ChargeCapacity; i++)
        {
            t.AddHeat(pos, 255);
            home.AbsorbHeatFrom(t, pos, 0, 1e6, out _, out _, out _);
            home.Advance(t); // the radiator vents the reactor, so the relay keeps delivering
        }
        Assert.Equal(home.ChargeCapacity, home.Charge, precision: 6);
        double chargeBefore = home.Charge;
        double ledgerBefore = t.Ledger.ConvertedToCharge;

        t.AddHeat(pos, 90);
        home.AbsorbHeatFrom(t, pos, 0, 1e6, out _, out _, out _);

        Assert.Equal(chargeBefore, home.Charge, precision: 6);
        Assert.Equal(ledgerBefore, t.Ledger.ConvertedToCharge, precision: 6);
    }

    /// <summary>Charge made per unit of heat a relay delivered from ground heated by <paramref name="amount"/>.</summary>
    private static double ChargePerHeat(int amount)
    {
        var s = RelayHeatTests.CreateScenario();
        s.World.Terrain.AddHeatRadius(s.RelayPos, amount, 6);
        var relay = RelayHeatTests.PlantRelay(s);
        double before = s.Base.Charge, pumped = 0.0;
        for (int i = 0; i < 48; i++)
        {
            s.World.Advance(_ => default);
            pumped += relay.LastPumpedEnergy;
        }
        Assert.True(pumped > 0.0, "the relay should pump");
        return (s.Base.Charge - before) / pumped;
    }

    [Fact]
    public void Relay_OnHotGround_MakesMoreChargePerHeatThanOnWarmGround()
    {
        double warm = ChargePerHeat(30), hot = ChargePerHeat(200);
        Assert.True(warm > 0.0, "even warm ground makes a little charge");
        Assert.True(hot > warm * 1.5, $"hot sources are worth more. warm={warm:0.000}, hot={hot:0.000}");
    }

    [Fact]
    public void PlantingAHarvester_FillsItsBatteryFromTheBaseStore()
    {
        var s = RelayHeatTests.CreateScenario();
        double before = s.Base.Charge;
        var harvester = Plant(s, MachineType.Harvester, s.RelayPos);

        Assert.Equal(Tweaks.Machine.HarvesterBatteryCapacity, harvester.Charge, precision: 6);
        Assert.Equal(before - harvester.Charge, s.Base.Charge, precision: 6);
    }

    [Fact]
    public void Harvester_SpendsItsBatteryAsItWorks_AndStopsWhenFlat()
    {
        var s = RelayHeatTests.CreateScenario();
        HarvesterHeatTests.SeedOre(s);
        RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(0, s.DirY * 3));
        var harvester = Plant(s, MachineType.Harvester, s.RelayPos);
        harvester.Charge = Tweaks.Machine.HarvesterHeatPerHarvest * 3.5;   // three actions' worth, no pylon to go to

        Advance(s, Tweaks.Perf.TargetFps * 10);

        Assert.True(harvester.Charge < Tweaks.Machine.HarvesterHeatPerHarvest, $"charge left={harvester.Charge:0}");
        double flat = harvester.Charge;
        int storeBefore = s.Base.Store.Ore;
        Advance(s, Tweaks.Perf.TargetFps * 5);
        Assert.Equal(storeBefore, s.Base.Store.Ore);
        Assert.Equal(flat, harvester.Charge, precision: 6);
    }

    [Fact]
    public void Pylon_ChargesABatteryInItsLineOfSight_FromTheBaseStore()
    {
        var s = RelayHeatTests.CreateScenario();
        var pylon = Plant(s, MachineType.Pylon, PylonSpot(s));
        var harvester = Plant(s, MachineType.Harvester, PylonSpot(s) + new Offset(0, s.DirY * 8));
        Advance(s, 12);   // let the link map power the pylon
        harvester.Charge = Tweaks.Machine.HarvesterBatteryCapacity * 0.5;
        double total = s.Base.Charge + harvester.Charge;

        Advance(s, Tweaks.Perf.TargetFps);

        Assert.True(pylon.LinkPoint!.IsPowered, "the pylon should be on the base's network");
        Assert.True(harvester.Charge > Tweaks.Machine.HarvesterBatteryCapacity * 0.5,
            $"the pylon should have charged the harvester. charge={harvester.Charge:0}");
        // Charge only moves from the base to the battery; the harvester also spent some digging.
        Assert.True(s.Base.Charge + harvester.Charge <= total + 1e-6);
    }

    [Fact]
    public void Harvester_LowOnCharge_GoesBackToAPylon_AndResumesWhenCharged()
    {
        var s = RelayHeatTests.CreateScenario();
        var pylon = Plant(s, MachineType.Pylon, PylonSpot(s));
        var harvester = Plant(s, MachineType.Harvester, s.RelayPos);
        RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(0, s.DirY * 3));
        Advance(s, 12);
        harvester.Charge = Tweaks.Machine.HarvesterBatteryCapacity * 0.1;
        int range2 = Tweaks.Machine.PylonRange * Tweaks.Machine.PylonRange;
        Assert.True(Position.DistanceSquared(harvester.Position, pylon.Position) > range2, "the harvester starts out of the pylon's reach");

        bool returned = false, resumed = false;
        for (int i = 0; i < Tweaks.Perf.TargetFps * 60 && !resumed; i++)
        {
            s.World.Advance(_ => default);
            if (harvester.IsReturning && Position.DistanceSquared(harvester.Position, pylon.Position) <= range2)
                returned = true;
            if (returned && !harvester.IsReturning)
                resumed = true;
        }

        Assert.True(returned, "a low harvester should crawl back into a pylon's reach");
        Assert.True(resumed, "once charged it should go back to work");
        Assert.True(harvester.ChargeFraction >= Tweaks.Machine.HarvesterResumeAbove - 0.05f, $"charge={harvester.ChargeFraction:0.00}");
    }

    [Fact]
    public void Harvester_ReturningThroughDirtOutsideItsWorkArea_CarvesItsWayIntoTheBeam()
    {
        var s = RelayHeatTests.CreateScenario();
        var pylon = Plant(s, MachineType.Pylon, PylonSpot(s));
        var harvester = Plant(s, MachineType.Harvester, s.RelayPos);
        RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(0, s.DirY * 3));
        Advance(s, 12);
        // A dirt plug across the tunnel just past the pylon, outside the harvester's work area (it is anchored 26
        // cells further out with a 20-cell range), so the pylon cannot see it until it digs through.
        var plug = PylonSpot(s) + new Offset(0, s.DirY * 3);
        for (int dx = -3; dx <= 3; dx++)
            for (int dy = 0; dy <= 1; dy++)
                s.World.Terrain.SetPixel(plug + new Offset(dx, s.DirY * dy), Emberward.Core.Terrain.TerrainPixel.Dirt);
        harvester.Charge = Tweaks.Machine.HarvesterBatteryCapacity * 0.1;

        bool charged = false;
        for (int i = 0; i < Tweaks.Perf.TargetFps * 60 && !charged; i++)
        {
            s.World.Advance(_ => default);
            charged = ReferenceEquals(harvester.ChargingFrom, pylon);
        }

        Assert.True(pylon.LinkPoint!.IsPowered, "the plug is beyond the pylon, so the pylon stays linked");
        Assert.True(charged, $"the harvester should carve through the plug into the pylon's beam. at {harvester.Position}");
    }

    [Fact]
    public void Harvester_CrawlsOverRegrowingDirt()
    {
        var s = RelayHeatTests.CreateScenario();
        var harvester = Plant(s, MachineType.Harvester, s.RelayPos);
        RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(0, s.DirY * -6));
        // Ore further down the tunnel, and the open tunnel between it and the harvester filled with regrowing dirt, as
        // when its walls start to recover. The line back to the relay stays open, so the harvester stays on the network.
        HarvesterHeatTests.SeedOre(s, along: 10);
        var t = s.World.Terrain;
        for (int dy = 1; dy <= 6; dy++)
            for (int dx = -6; dx <= 6; dx++)
            {
                var pos = s.RelayPos + new Offset(dx, s.DirY * dy);
                if (t.GetPixelRaw(pos) == Emberward.Core.Terrain.TerrainPixel.Blank && Math.Abs(dx) + Math.Abs(dy) > 0)
                    t.SetPixel(pos, Emberward.Core.Terrain.TerrainPixel.DirtGrow);
            }
        var start = harvester.Position;

        Advance(s, Tweaks.Perf.TargetFps * 10);

        Assert.NotEqual(start, harvester.Position);
    }

    [Fact]
    public void ThermalEnergyPlusCharge_BalancesWithTheLedger()
    {
        var s = RelayHeatTests.CreateScenario();
        s.World.Terrain.AddHeatRadius(s.RelayPos, 150, 8);
        Plant(s, MachineType.Pylon, PylonSpot(s));
        Plant(s, MachineType.Harvester, s.RelayPos);
        RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(0, s.DirY * 3));
        var ledger = s.World.Terrain.Ledger;

        double start = s.World.SumSystemThermalEnergy() + s.World.SumSystemCharge();
        double startExternal = ledger.Injected - ledger.TotalSunk;
        double maxResidual = 0.0;
        for (int i = 0; i < Tweaks.Perf.TargetFps * 20; i++)
        {
            s.World.Advance(_ => default);
            double now = s.World.SumSystemThermalEnergy() + s.World.SumSystemCharge();
            double residual = (now - start) - (ledger.Injected - ledger.TotalSunk - startExternal);
            maxResidual = Math.Max(maxResidual, Math.Abs(residual));
        }

        Assert.True(ledger.ConvertedToCharge != 0.0, "the loop should have converted energy");
        Assert.True(maxResidual < 1.0, $"heat and charge must add up. residual={maxResidual:0.0000}");
    }
}
