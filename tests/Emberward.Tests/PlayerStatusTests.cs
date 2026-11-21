using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Input;
using Emberward.Core.Resources;
using Emberward.Core.Types;
using Emberward.Core.Network;
using Emberward.Core.Entities.Bases;

namespace Emberward.Tests;

/// <summary>
/// The Core queries the HUD and the warning sounds ask instead of computing game rules themselves: warnings, gauge
/// levels, fill ratios, the heat gauge's range and the hint's base and build questions.
/// </summary>
public class PlayerStatusTests
{
    private static (World World, Tank Tank) Create() => TestHelpers.CreateSeededWorldWithTank(enableTerrainRegrowth: false);

    private static void SetShield(Tank tank, float fraction)
    {
        int capacity = tank.Reactor.HealthCapacity;
        tank.Reactor.Add(new ReactorState(0, capacity));
        tank.Reactor.Exhaust(new ReactorState(0, capacity - (int)(fraction * capacity)));
    }

    [Fact]
    public void RunningHot_LatchesWithHysteresis()
    {
        var (_, tank) = Create();
        float safe = Tweaks.Tank.HeatSafeMax;

        tank.Heat = 0.75f * safe;
        Assert.False(tank.IsRunningHot(latched: false));
        Assert.True(tank.IsRunningHot(latched: true));
        tank.Heat = 0.85f * safe;
        Assert.True(tank.IsRunningHot(latched: false));
        tank.Heat = 0.65f * safe;
        Assert.False(tank.IsRunningHot(latched: true));
    }

    [Fact]
    public void ShieldLow_LatchesWithHysteresis()
    {
        var (_, tank) = Create();

        SetShield(tank, 0.27f);
        Assert.False(tank.IsShieldLow(latched: false));
        Assert.True(tank.IsShieldLow(latched: true));
        SetShield(tank, 0.20f);
        Assert.True(tank.IsShieldLow(latched: false));
        SetShield(tank, 0.35f);
        Assert.False(tank.IsShieldLow(latched: true));
    }

    [Theory]
    [InlineData(1.00f, StatusLevel.Ok)]
    [InlineData(0.51f, StatusLevel.Ok)]
    [InlineData(0.50f, StatusLevel.Warn)]
    [InlineData(0.26f, StatusLevel.Warn)]
    [InlineData(0.25f, StatusLevel.Bad)]
    [InlineData(0.00f, StatusLevel.Bad)]
    public void ShieldLevel_FollowsTheShieldFraction(float fraction, StatusLevel expected)
    {
        var (_, tank) = Create();
        SetShield(tank, fraction);
        Assert.Equal(fraction, tank.ShieldFraction, 3);
        Assert.Equal(expected, tank.ShieldLevel);
    }

    [Fact]
    public void HoldFill_IsTheOreHoldAgainstItsCapacity()
    {
        var (_, tank) = Create();
        tank.Hold.Ore = 0;
        Assert.Equal(0f, tank.HoldFill);
        tank.Hold.Add(tank.OreHoldCapacity / 4);
        Assert.Equal(0.25f, tank.HoldFill);
        tank.Hold.Add(tank.OreHoldCapacity);
        Assert.Equal(1f, tank.HoldFill);
    }

    [Fact]
    public void CoolingDown_UntilTheTankIsBackNearZero()
    {
        var (_, tank) = Create();
        tank.Heat = 10f;
        Assert.True(tank.IsCoolingDown);
        tank.Heat = 0.5f * Tweaks.Tank.FullyCooledMargin;
        Assert.False(tank.IsCoolingDown);
        tank.Heat = -10f;
        Assert.False(tank.IsCoolingDown);
    }

    [Fact]
    public void AboveSafeHeat_OnlyPastTheSafeMaximum()
    {
        var (_, tank) = Create();
        tank.Heat = Tweaks.Tank.HeatSafeMax;
        Assert.False(tank.IsAboveSafeHeat);
        tank.Heat = Tweaks.Tank.HeatSafeMax + 0.1f;
        Assert.True(tank.IsAboveSafeHeat);
    }

    [Fact]
    public void OffNetworkUrgent_WhenTheFloorIsCloseToOverheating()
    {
        var (_, tank) = Create();
        // A fresh tank has its floor far below the safe maximum.
        Assert.True(tank.OffNetworkSecondsToOverheat >= Tweaks.Tank.OffNetworkUrgentSeconds);
        Assert.False(tank.IsOffNetworkUrgent);
    }

    [Fact]
    public void HeatGauge_RunsFromPastFrostbiteToPastTheSafeMaximum()
    {
        Assert.Equal(Tweaks.Tank.FrostbiteTemperature * Tweaks.Tank.HeatGaugeFrostMargin, Tank.HeatGaugeMin);
        Assert.True(Tank.HeatGaugeMin < Tweaks.Tank.FrostbiteTemperature);
        Assert.Equal(Tweaks.Tank.HeatSafeMax * Tweaks.Tank.HeatGaugeOverheatMargin, Tank.HeatGaugeMax);
        Assert.True(Tank.HeatGaugeMax > Tweaks.Tank.HeatSafeMax);
    }

    [Fact]
    public void HasLeftBase_LatchesOnceTheLiveTankIsOutsideEveryBase()
    {
        var (world, tank) = Create();
        world.Advance(_ => default);
        Assert.False(tank.HasLeftBase);

        tank.Position = tank.Base!.Position + new Offset(0, Tweaks.Base.BaseSize);
        Assert.False(world.IsInsideAnyBase(tank.Position));
        world.Advance(_ => default);
        Assert.True(tank.HasLeftBase);

        tank.Position = tank.Base.SpawnPosition;
        world.Advance(_ => default);
        Assert.True(tank.HasLeftBase);
    }

    [Fact]
    public void IsInsideAnyBase_CountsEveryBase()
    {
        var (world, _) = Create();
        foreach (var tankBase in world.TankBases.Bases)
            Assert.True(world.IsInsideAnyBase(tankBase.Position));
        Assert.False(world.IsInsideAnyBase(new Position(0, 0)));
    }

    [Fact]
    public void QuoteBuild_Docked_TheStorePaysFirst_ThenTheHold_ElseNotEnoughOre()
    {
        var (_, tank) = Create();
        int relay = Tweaks.Ore.RelayCost;
        var store = tank.Base!.Store;
        Assert.True(tank.IsDockedAtOwnBase);

        store.Ore = relay;
        tank.Hold.Ore = relay;
        var quote = tank.QuoteBuild(MachineType.Relay);
        Assert.Equal((OrePayer.BaseStore, relay, relay, BuildRefusal.None, true),
            (quote.Payer, quote.Cost, quote.Stock, quote.Refusal, quote.InNetwork));

        store.Ore = relay - 1;
        quote = tank.QuoteBuild(MachineType.Relay);
        Assert.Equal(OrePayer.Hold, quote.Payer);

        tank.Hold.Ore = 1;
        quote = tank.QuoteBuild(MachineType.Relay);
        Assert.False(quote.CanAfford);
        Assert.Equal(BuildRefusal.NotEnoughOre, quote.Refusal);
        Assert.Equal(relay - 1, quote.Stock);
    }

    [Fact]
    public void CarriedPlantLink_IsNoNetworkWithEmptyHandsOrInsideABase_ElseTheLinkPreview()
    {
        var (world, tank) = Create();
        Assert.Equal(LinkPreviewStatus.NoNetwork, world.CarriedPlantLink(tank));

        world.Advance(i => i == 0 ? new ControllerOutput { BuildPrimary = true } : default);
        Assert.Equal(MachineType.Relay, tank.CarriedMachineType);
        Assert.Equal(LinkPreviewStatus.NoNetwork, world.CarriedPlantLink(tank));

        tank.Position = tank.Base!.Position + new Offset(0, Tweaks.Base.BaseSize);
        Assert.Equal(world.PreviewPlacement(MachineType.Relay, tank.Position).Link.Status, world.CarriedPlantLink(tank));
    }

    [Theory]
    [InlineData(0.3, StatusLevel.Ok)]
    [InlineData(0.29, StatusLevel.Warn)]
    [InlineData(0.1, StatusLevel.Warn)]
    [InlineData(0.09, StatusLevel.Bad)]
    public void ChargeLevel_FollowsTheChargeFill(double fill, StatusLevel expected)
    {
        var (_, tank) = Create();
        var tankBase = tank.Base!;
        tankBase.DrawCharge(tankBase.Charge - fill * tankBase.ChargeCapacity);
        Assert.Equal(fill, tankBase.ChargeFill, 6);
        Assert.Equal(expected, tankBase.ChargeLevel);
    }

    [Theory]
    [InlineData(0.0, StatusLevel.Ok)]
    [InlineData(0.59, StatusLevel.Ok)]
    [InlineData(0.6, StatusLevel.Warn)]
    [InlineData(0.89, StatusLevel.Warn)]
    [InlineData(0.9, StatusLevel.Bad)]
    [InlineData(1.0, StatusLevel.Bad)]
    public void HeatStoreLevel_WarnsWhileFilling_BadWhenFull(double fill, StatusLevel expected) =>
        Assert.Equal(expected, TankBase.HeatStoreLevelOf(fill));

    [Fact]
    public void HeatStoreFullWithRelays_NeedsAPoweredRelay()
    {
        var full = new NetworkStatus(0, 0, 0, 0, 0, 0, 0, Relays: 1, PoweredRelays: 0, PumpingRelays: 0,
            RelayInflow: 0, RadiatorInflow: 0, VentCapacity: RelayHeatTests.CreateScenario().Base.VentCapacityPerTick, HeatStoreFill: 0.95);
        Assert.Equal(StatusLevel.Bad, full.HeatStoreLevel);
        Assert.False(full.IsHeatStoreFullWithRelays);
        Assert.True((full with { PoweredRelays = 1 }).IsHeatStoreFullWithRelays);
        Assert.False((full with { PoweredRelays = 1, HeatStoreFill = 0.5 }).IsHeatStoreFullWithRelays);
    }

    [Fact]
    public void RadiatorLoad_IsInflowOverVentCapacity_BadPastOne()
    {
        var tankBase = RelayHeatTests.CreateScenario().Base;
        double vent = tankBase.VentCapacityPerTick;
        Assert.Equal(0f, tankBase.RadiatorLoad(0.0));
        Assert.Equal(1f, tankBase.RadiatorLoad(vent));
        Assert.Equal(StatusLevel.Ok, TankBase.RadiatorLoadLevel(tankBase.RadiatorLoad(0.5 * vent)));
        Assert.Equal(StatusLevel.Warn, TankBase.RadiatorLoadLevel(tankBase.RadiatorLoad(0.8 * vent)));
        Assert.Equal(StatusLevel.Warn, TankBase.RadiatorLoadLevel(1.0f));
        Assert.Equal(StatusLevel.Bad, TankBase.RadiatorLoadLevel(tankBase.RadiatorLoad(1.2 * vent)));
    }
}
