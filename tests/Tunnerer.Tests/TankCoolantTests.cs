using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Input;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;
using static Tunnerer.Tests.TankHeatTestHelpers;

namespace Tunnerer.Tests;

/// <summary>
/// Pre-cooling: a tank docked at its own base is chilled below ambient (its coolant charge), which gives it a larger
/// heat budget before overheating. The charge is only spent by absorbing real heat.
/// </summary>
public class TankCoolantTests
{
    private static void Advance(World world, int ticks)
    {
        for (int i = 0; i < ticks; i++)
            world.Advance(_ => default);
    }

    private static (World world, Tank tank, Position pos) OpenGroundScenario(int seed, float startHeat)
    {
        var (world, tank) = CreateWorldWithPrimaryTank(seed);
        var pos = GetWorldCenter(world);
        tank.Position = pos;
        ClearArea(world.Terrain, pos, radius: 8);
        SetTankHeat(tank, startHeat);
        return (world, tank, pos);
    }

    [Fact]
    public void Tank_DockedAtOwnBase_IsPreCooledToFullCoolant_AndEnergyBalances()
    {
        var (world, tank) = CreateWorldWithPrimaryTank(seed: 5151);
        SetTankHeat(tank, 0f);

        double start = world.SumSystemThermalEnergy();
        double startNet = world.Terrain.Ledger.NetExternal;
        double maxResidual = 0.0;
        for (int i = 0; i < 150; i++)
        {
            world.Advance(_ => default);
            double residual = (world.SumSystemThermalEnergy() - start) - (world.Terrain.Ledger.NetExternal - startNet);
            maxResidual = Math.Max(maxResidual, Math.Abs(residual));
        }

        Assert.True(tank.Heat <= -Tweaks.Tank.CoolantCapacity + 0.5f, $"the tank should be fully pre-cooled. heat={tank.Heat:0.00}");
        Assert.True(tank.CoolantFraction > 0.98f);
        Assert.Equal(Tank.HeatBudgetCapacity, tank.HeatBudget, precision: 0);
        Assert.True(maxResidual < 0.5, $"refrigeration is a transfer to the reservoir and must not create energy. residual={maxResidual:0.0000}");
    }

    [Fact]
    public void Tank_AtForeignBase_IsNotRefrigerated()
    {
        var (world, tank) = CreateWorldWithPrimaryTank(seed: 5151);
        var foreignBase = world.TankBases.Bases.First(b => b != tank.Base);
        tank.Position = foreignBase.Position;
        SetTankHeat(tank, 0f);

        Advance(world, 120);

        Assert.True(tank.Heat > -1f, $"only the tank's own base pre-cools it. heat={tank.Heat:0.00}");
    }

    [Fact]
    public void Refrigeration_StopsWhenTheBaseReservoirIsFull()
    {
        var (world, tank) = CreateWorldWithPrimaryTank(seed: 5151);
        SetTankHeat(tank, 0f);
        tank.Base!.Reactor.Current.Heat = tank.Base.Reactor.HeatCapacity; // reservoir full

        // Step only the tank so the radiator does not make room.
        AdvanceTank(tank, world, frames: 30);

        Assert.True(tank.Heat > -0.01f, $"a full reservoir cannot take the tank's heat. heat={tank.Heat:0.000}");
    }

    [Fact]
    public void AcceptTankHeat_IsLimitedByTheReservoirRoom()
    {
        var (world, tank) = CreateWorldWithPrimaryTank(seed: 5151);
        var tankBase = tank.Base!;
        double perUnit = Tweaks.Base.ReactorEnergyPerHeatUnit;

        tankBase.Reactor.Current.Heat = new Core.Resources.Heat((int)tankBase.Reactor.HeatCapacity - 1);
        double room = tankBase.ReactorRoomEnergy;
        Assert.Equal(perUnit, room, precision: 3);

        double accepted = tankBase.AcceptTankHeat(10 * perUnit);
        Assert.Equal(room, accepted, precision: 3);
        Assert.Equal(0.0, tankBase.AcceptTankHeat(1000.0), precision: 3);
    }

    [Fact]
    public void ColdTank_KeepsItsCharge_InAmbientAir()
    {
        var (world, tank, _) = OpenGroundScenario(seed: 5252, startHeat: -40f);

        Advance(world, 300);

        Assert.True(Math.Abs(tank.Heat - -40f) < 0.5f,
            $"ambient air must not quietly spend the coolant. heat={tank.Heat:0.00}");
    }

    [Fact]
    public void ColdTank_AbsorbsRealHeat_SpendingItsCharge_AndEnergyBalances()
    {
        var (world, tank, pos) = OpenGroundScenario(seed: 5353, startHeat: -40f);
        world.Terrain.AddHeatRadius(pos, amount: 150, radius: 6);

        double start = world.SumSystemThermalEnergy();
        double startNet = world.Terrain.Ledger.NetExternal;
        double maxResidual = 0.0;
        for (int i = 0; i < 200; i++)
        {
            world.Advance(_ => default);
            double residual = (world.SumSystemThermalEnergy() - start) - (world.Terrain.Ledger.NetExternal - startNet);
            maxResidual = Math.Max(maxResidual, Math.Abs(residual));
        }

        Assert.True(tank.Heat > -35f, $"the tank should have absorbed heat. heat={tank.Heat:0.00}");
        Assert.True(tank.Heat < Tweaks.Tank.HeatSafeMax, $"this heat source must not overheat the tank. heat={tank.Heat:0.00}");
        Assert.True(maxResidual < 0.5, $"tank <-> grid exchange must conserve energy. residual={maxResidual:0.0000}");
    }

    [Fact]
    public void PreCooledTank_PeaksLowerThanAnUncooledOne_InTheSameHeat()
    {
        // The same one-off hot zone: a pre-cooled tank absorbs heat at the same rate but starts lower, so it keeps
        // its distance from the overheating threshold.
        float PeakHeat(float startHeat)
        {
            var (world, tank, pos) = OpenGroundScenario(seed: 5454, startHeat: startHeat);
            world.Terrain.AddHeatRadius(pos, amount: 160, radius: 6);
            float peak = tank.Heat;
            for (int i = 0; i < 200; i++)
            {
                world.Advance(_ => default);
                peak = Math.Max(peak, tank.Heat);
            }

            Assert.Equal(Tweaks.Tank.InitialHealth, tank.Reactor.Health); // never took overheat damage
            return peak;
        }

        float cooledPeak = PeakHeat(-Tweaks.Tank.CoolantCapacity);
        float warmPeak = PeakHeat(0f);

        Assert.True(warmPeak > 10f, $"the scenario must actually heat the tank. warm={warmPeak:0.0}");
        // Not a full CoolantCapacity lower: the heat source is finite and the cold tank soaks up more of it,
        // so the zone cools before the warm tank would have peaked.
        Assert.True(cooledPeak <= warmPeak - 10f,
            $"the coolant charge should keep the tank clearly cooler. cooled={cooledPeak:0.0}, warm={warmPeak:0.0}");
    }

    [Fact]
    public void Overheat_DamageStartsOnlyAboveTheSafeMax_RegardlessOfCoolant()
    {
        int HealthAfterOneTick(float heat)
        {
            var (world, tank, _) = OpenGroundScenario(seed: 5555, startHeat: heat);
            var ctx = TankHeatTestHelpers.CreateContext(world);
            tank.Advance(in ctx, default);
            return tank.Reactor.Health;
        }

        Assert.Equal(Tweaks.Tank.InitialHealth, HealthAfterOneTick(-Tweaks.Tank.CoolantCapacity));
        Assert.Equal(Tweaks.Tank.InitialHealth, HealthAfterOneTick(Tweaks.Tank.HeatSafeMax - 5f));
        Assert.True(HealthAfterOneTick(Tweaks.Tank.HeatSafeMax + 40f) < Tweaks.Tank.InitialHealth,
            "above the safe max the tank must take overheat damage");
    }

    [Fact]
    public void HeatBudget_RunsFromFullCoolantDownToTheOverheatThreshold()
    {
        var (world, tank) = CreateWorldWithPrimaryTank(seed: 5151);

        SetTankHeat(tank, -Tweaks.Tank.CoolantCapacity);
        Assert.Equal(Tweaks.Tank.HeatSafeMax + Tweaks.Tank.CoolantCapacity, tank.HeatBudget, precision: 3);
        Assert.Equal(1f, tank.CoolantFraction, precision: 3);

        SetTankHeat(tank, 0f);
        Assert.Equal(Tweaks.Tank.HeatSafeMax, tank.HeatBudget, precision: 3);
        Assert.Equal(0f, tank.CoolantFraction, precision: 3);

        SetTankHeat(tank, Tweaks.Tank.HeatSafeMax);
        Assert.Equal(0f, tank.HeatBudget, precision: 3);
    }
}
