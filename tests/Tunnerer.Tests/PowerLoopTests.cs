using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

namespace Tunnerer.Tests;

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
        Assert.Equal(0.0, tankBase.EngineEfficiency(Tweaks.World.ThermalMaterials[ThermalMaterial.Base].FixedTemperature.GetValueOrDefault()));
        Assert.Equal(0.0, tankBase.EngineEfficiency(-20));
        double warm = tankBase.EngineEfficiency(30), hot = tankBase.EngineEfficiency(100), extreme = tankBase.EngineEfficiency(10_000);
        Assert.True(warm > 0.0 && warm < hot, $"warm={warm}, hot={hot}");
        Assert.Equal(Tweaks.Base.EngineMaxEfficiency, extreme, precision: 6);
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
        var harvester = Plant(s, MachineType.Harvester, s.RelayPos);
        harvester.Charge = Tweaks.Machine.HarvesterHeatPerHarvest * 3.5;   // three actions' worth, no pylon to go to

        Advance(s, Tweaks.Perf.TargetFps * 10);

        Assert.True(harvester.Charge < Tweaks.Machine.HarvesterHeatPerHarvest, $"charge left={harvester.Charge:0}");
        double flat = harvester.Charge;
        int storeBefore = s.Base.Materials.Dirt;
        Advance(s, Tweaks.Perf.TargetFps * 5);
        Assert.Equal(storeBefore, s.Base.Materials.Dirt);
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
                s.World.Terrain.SetPixel(plug + new Offset(dx, s.DirY * dy), Tunnerer.Core.Terrain.TerrainPixel.Dirt);
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
        // Fill the open tunnel around the harvester with regrowing dirt, as when its walls start to recover.
        var t = s.World.Terrain;
        for (int dy = -6; dy <= 6; dy++)
            for (int dx = -6; dx <= 6; dx++)
            {
                var pos = s.RelayPos + new Offset(dx, dy);
                if (t.GetPixelRaw(pos) == Tunnerer.Core.Terrain.TerrainPixel.Blank && Math.Abs(dx) + Math.Abs(dy) > 0)
                    t.SetPixel(pos, Tunnerer.Core.Terrain.TerrainPixel.DirtGrow);
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
