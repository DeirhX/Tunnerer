using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Input;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Thermal;
using Tunnerer.Core.Types;
using static Tunnerer.Tests.TankHeatTestHelpers;

namespace Tunnerer.Tests;

/// <summary>
/// Tank temperature (DESIGN.md §4.7): the base pulls a docked tank toward 0 at a rate set by how far it is from it;
/// a tank exchanges heat both ways with what it stands in; cold slows it and deep cold gives frostbite, as heat above
/// the safe maximum burns it. Frost pockets are the world's cold sources.
/// </summary>
public class TankTemperatureTests
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
    public void DockedHotTank_CoolsTowardZero_FastAtFirst_NeverBelow_AndEnergyBalances()
    {
        var (world, tank) = CreateWorldWithPrimaryTank(seed: 5151);
        SetTankHeat(tank, 80f);

        double start = world.SumSystemThermalEnergy();
        double startNet = world.Terrain.Ledger.NetExternal;
        double maxResidual = 0.0;
        float afterOneSecond = 0f;
        for (int i = 0; i < Tweaks.Perf.TargetFps * 4; i++)
        {
            world.Advance(_ => default);
            if (i == Tweaks.Perf.TargetFps - 1)
                afterOneSecond = tank.Heat;
            double residual = (world.SumSystemThermalEnergy() - start) - (world.Terrain.Ledger.NetExternal - startNet);
            maxResidual = Math.Max(maxResidual, Math.Abs(residual));
        }

        Assert.True(afterOneSecond < 40f, $"a hot tank should shed most of its heat in the first second. heat={afterOneSecond:0.0}");
        Assert.InRange(tank.Heat, -0.5f, 5f);
        Assert.True(maxResidual < 0.5, $"docking is a transfer to the reservoir and must not create energy. residual={maxResidual:0.0000}");
    }

    [Fact]
    public void DockedTank_DoesNotGetColdAtTheBase()
    {
        var (world, tank) = CreateWorldWithPrimaryTank(seed: 5151);
        SetTankHeat(tank, 0f);

        Advance(world, Tweaks.Perf.TargetFps * 6);

        Assert.True(tank.Heat > -0.5f, $"the base settles a tank near 0, not below it. heat={tank.Heat:0.00}");
    }

    [Fact]
    public void ForeignBase_DoesNotPumpTheTanksHeat()
    {
        float HeatAfter(bool own)
        {
            var (world, tank) = CreateWorldWithPrimaryTank(seed: 5151);
            if (!own)
                tank.Position = world.TankBases.Bases.First(b => b != tank.Base).Position;
            SetTankHeat(tank, 60f);
            Advance(world, Tweaks.Perf.TargetFps);
            return tank.Heat;
        }

        Assert.True(HeatAfter(own: true) < HeatAfter(own: false) - 5f, "only the tank's own base pumps its heat away");
    }

    [Fact]
    public void DockPump_StopsWhenTheBaseReservoirIsFull()
    {
        float HeatAfter(bool full)
        {
            var (world, tank) = CreateWorldWithPrimaryTank(seed: 5151);
            SetTankHeat(tank, 60f);
            if (full)
                tank.Base!.Reactor.Current.Heat = tank.Base.Reactor.HeatCapacity;
            AdvanceTank(tank, world, frames: 24);   // step only the tank so the radiator does not make room
            return tank.Heat;
        }

        Assert.True(HeatAfter(full: true) > HeatAfter(full: false) + 5f, "a full reservoir cannot take the tank's heat");
    }

    [Fact]
    public void AcceptTankHeat_IsLimitedByTheReservoirRoom()
    {
        var (_, tank) = CreateWorldWithPrimaryTank(seed: 5151);
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
    public void ColdTank_WarmsTowardItsSurroundings_AndEnergyBalances()
    {
        var (world, tank, _) = OpenGroundScenario(seed: 5252, startHeat: -40f);
        double start = world.SumSystemThermalEnergy();
        double startNet = world.Terrain.Ledger.NetExternal;
        double maxResidual = 0.0;

        for (int i = 0; i < 300; i++)
        {
            world.Advance(_ => default);
            double residual = (world.SumSystemThermalEnergy() - start) - (world.Terrain.Ledger.NetExternal - startNet);
            maxResidual = Math.Max(maxResidual, Math.Abs(residual));
        }

        Assert.True(tank.Heat > -30f, $"ambient ground should warm a cold tank. heat={tank.Heat:0.00}");
        Assert.True(tank.Heat < 0.5f, $"but not past ambient. heat={tank.Heat:0.00}");
        Assert.True(maxResidual < 0.5, $"tank <-> grid exchange must conserve energy. residual={maxResidual:0.0000}");
    }

    [Fact]
    public void Damage_ComesOnlyAboveTheSafeMax_OrBelowTheFrostbiteTemperature()
    {
        int HealthAfterOneTick(float heat)
        {
            var (world, tank, _) = OpenGroundScenario(seed: 5555, startHeat: heat);
            var ctx = CreateContext(world);
            tank.Advance(in ctx, default);
            return tank.Reactor.Health;
        }

        Assert.Equal(Tweaks.Tank.InitialHealth, HealthAfterOneTick(Tweaks.Tank.FrostbiteTemperature + 5f));
        Assert.Equal(Tweaks.Tank.InitialHealth, HealthAfterOneTick(Tweaks.Tank.HeatSafeMax - 5f));
        Assert.True(HealthAfterOneTick(Tweaks.Tank.HeatSafeMax + 40f) < Tweaks.Tank.InitialHealth, "overheating burns");
        Assert.True(HealthAfterOneTick(Tweaks.Tank.FrostbiteTemperature - 30f) < Tweaks.Tank.InitialHealth, "deep cold bites");
    }

    [Fact]
    public void Cold_SlowsTheTank_Gradually()
    {
        Assert.Equal(1f, TankHeatModel.ColdSpeedFactor(20f));
        Assert.Equal(1f, TankHeatModel.ColdSpeedFactor(Tweaks.Tank.ColdSlowStart));
        float chilly = TankHeatModel.ColdSpeedFactor(-15f);
        Assert.InRange(chilly, Tweaks.Tank.ColdMinSpeed + 0.01f, 0.99f);
        Assert.Equal(Tweaks.Tank.ColdMinSpeed, TankHeatModel.ColdSpeedFactor(-200f), precision: 5);
    }

    [Fact]
    public void AColdTank_CoversLessGround()
    {
        int CellsDriven(float heat)
        {
            // Down the scenario's open tunnel, starting just outside the base door.
            var s = RelayHeatTests.CreateScenario();
            var start = s.Base.Position + new Offset(0, s.DirY * (Tweaks.Base.BaseSize / 2 + 6));
            s.Tank.Position = start;
            var ctx = CreateContext(s.World);
            var drive = new ControllerOutput { MoveSpeed = new Offset(0, s.DirY) };
            for (int i = 0; i < 24; i++)
            {
                SetTankHeat(s.Tank, heat);   // hold the temperature so only speed differs
                s.Tank.Advance(in ctx, drive);
            }
            return Math.Abs(s.Tank.Position.Y - start.Y);
        }

        int warm = CellsDriven(10f), cold = CellsDriven(-60f);
        Assert.True(warm >= 20, $"warm tank drove {warm}");
        Assert.True(cold <= warm * (Tweaks.Tank.ColdMinSpeed + 0.1f), $"a frozen tank should crawl. warm={warm}, cold={cold}");
    }

    [Fact]
    public void FrostRock_IsAPinnedIndestructibleColdSource()
    {
        Assert.True(Pixel.IsBlockingCollision(TerrainPixel.FrostRock));
        Assert.False(Pixel.IsDiggable(TerrainPixel.FrostRock));
        Assert.False(Pixel.IsRock(TerrainPixel.FrostRock), "ordinary rock can be blasted away; a pocket must not be");
        Assert.Equal(ThermalMaterial.FrostRock, Pixel.GetThermalMaterial(TerrainPixel.FrostRock));
        Assert.True(TerrainHeatEngine.IsFixedTemperatureMaterial(ThermalMaterial.FrostRock));
        Assert.Equal(Tweaks.World.ThermalFixedFrostRockTemperature,
            new TerrainHeatEngine().GetFixedTemperature(ThermalMaterial.FrostRock));
    }

    [Fact]
    public void ATankBesideAFrostPocket_CoolsDown_AndThenGetsTooCold_EvenOffNetwork()
    {
        // At the far end of the scenario's tunnel, off its network, beside a pocket set into the tunnel wall.
        var s = RelayHeatTests.CreateScenario();
        var world = s.World;
        var tank = s.Tank;
        var at = s.Base.Position + new Offset(0, s.DirY * (Tweaks.Base.BaseSize / 2 + 46));
        world.Terrain.PlaceFrostRockPocket(at + new Offset(7, 0), radius: 4);
        tank.Position = at;
        SetTankHeat(tank, 60f);

        float minHeat = tank.Heat;
        float afterFive = 0f;
        for (int i = 0; i < Tweaks.Perf.TargetFps * 40; i++)
        {
            world.Advance(_ => default);
            minHeat = Math.Min(minHeat, tank.Heat);
            if (i == Tweaks.Perf.TargetFps * 5)
                afterFive = tank.Heat;
        }

        Assert.True(afterFive < 30f, $"a frost pocket is a quick place to shed heat. heat after 5 s={afterFive:0.0}");
        Assert.True(tank.IsOffNetwork, "the scenario is meant to be off network");
        Assert.True(minHeat < Tweaks.Tank.ColdSlowStart - 5f, $"stay too long and it gets cold. min={minHeat:0.0}");
    }

    [Fact]
    public void WorldFrostPockets_AreDeterministic_AndAwayFromBasesAndHotRock()
    {
        var a = TestHelpers.CreateSeededWorld(seed: 8181, hotRockPocketCount: 4, frostRockPocketCount: 4);
        var b = TestHelpers.CreateSeededWorld(seed: 8181, hotRockPocketCount: 4, frostRockPocketCount: 4);

        Assert.Equal(a.FrostRockPockets, b.FrostRockPockets);
        Assert.InRange(a.FrostRockPockets.Count, 1, 4);
        int minBaseSq = Tweaks.World.FrostRockMinDistanceFromBase * Tweaks.World.FrostRockMinDistanceFromBase;
        int minHotSq = Tweaks.World.FrostRockMinDistanceFromHot * Tweaks.World.FrostRockMinDistanceFromHot;
        foreach (var pocket in a.FrostRockPockets)
        {
            Assert.Equal(TerrainPixel.FrostRock, a.Terrain.GetPixelRaw(pocket));
            Assert.All(a.TankBases.Bases, tb => Assert.True(Position.DistanceSquared(pocket, tb.Position) >= minBaseSq));
            Assert.All(a.HotRockPockets, h => Assert.True(Position.DistanceSquared(pocket, h) >= minHotSq));
        }
    }
}
