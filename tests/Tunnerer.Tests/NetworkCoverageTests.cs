using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Links;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;
using static Tunnerer.Tests.TankHeatTestHelpers;

namespace Tunnerer.Tests;

/// <summary>
/// Network coverage (DESIGN.md §4.6): the base and its powered relays and pylons keep the ground around them cool for
/// their tank; outside that the tank's reactor runs hot, so how far it can roam depends on the network it has built.
/// </summary>
public class NetworkCoverageTests
{
    // The scenario's tunnel runs 50 cells beyond the base door; the relay spot is 30 beyond it.
    private static Position AlongTunnel(RelayHeatTests.Scenario s, int beyondDoor) =>
        s.Base.Position + new Offset(0, s.DirY * (Tweaks.Base.BaseSize / 2 + 1 + beyondDoor));

    private static void Park(RelayHeatTests.Scenario s, Position at, float heat)
    {
        s.Tank.Position = at;
        SetTankHeat(s.Tank, heat);
    }

    private static void Advance(RelayHeatTests.Scenario s, int ticks)
    {
        for (int i = 0; i < ticks; i++)
            s.World.Advance(_ => default);
    }

    [Fact]
    public void NearTheBase_TheTankIsCovered_AndCooledIntoTheBaseReactor()
    {
        var s = RelayHeatTests.CreateScenario();
        Park(s, AlongTunnel(s, 6), heat: 30f);
        double reactorBefore = s.Base.ReactorHeatEnergy;

        Advance(s, Tweaks.Perf.TargetFps * 2);

        Assert.True(s.Tank.Coverage.Covered, "close to the base the tank should be covered");
        Assert.False(s.Tank.IsOffNetwork);
        Assert.True(s.Tank.Heat < 25f, $"the network should cool the tank. heat={s.Tank.Heat:0.0}");
        Assert.True(s.Base.ReactorHeatEnergy > reactorBefore, "the tank's heat goes to the base reactor");
    }

    [Fact]
    public void InsideTheBase_TheNetworkIsNotWhatCoolsTheTank()
    {
        var s = RelayHeatTests.CreateScenario();
        Park(s, s.Base.Position + new Offset(8, 8), heat: 10f);   // docked, away from the base centre

        Advance(s, 4);

        Assert.False(s.Tank.Coverage.Covered, "the base cools a docked tank, so no network tether should show");
        Assert.False(s.Tank.IsOffNetwork);
    }

    [Fact]
    public void Cooling_NeverDipsIntoCoolant_OnlyDockingRefillsIt()
    {
        var s = RelayHeatTests.CreateScenario();
        Park(s, AlongTunnel(s, 6), heat: 2f);

        Advance(s, Tweaks.Perf.TargetFps * 3);

        Assert.InRange(s.Tank.Heat, -0.01f, 0.5f);
    }

    [Fact]
    public void FarFromTheNetwork_TheTankHeatsUpAtTheOffNetworkRate()
    {
        var s = RelayHeatTests.CreateScenario();
        Park(s, AlongTunnel(s, 48), heat: 0f);
        int ticks = Tweaks.Perf.TargetFps * 5;

        Advance(s, ticks);

        Assert.True(s.Tank.IsOffNetwork, "50 cells down an empty tunnel the tank is off its network");
        float expected = ticks * Tweaks.Tank.OffNetworkHeatPerSecond / Tweaks.Perf.TargetFps;
        Assert.InRange(s.Tank.Heat, expected - 0.5f, expected + 0.5f);
    }

    [Fact]
    public void OffNetwork_WorkHeatStillShedsAway_ButNotBelowTheRisingFloor()
    {
        var s = RelayHeatTests.CreateScenario();
        Park(s, AlongTunnel(s, 48), heat: 60f);   // a hot burst of digging, far from the network

        Advance(s, Tweaks.Perf.TargetFps * 8);

        Assert.True(s.Tank.Heat < 40f, $"work heat above the floor should still shed into the ground. heat={s.Tank.Heat:0.0}");
        Assert.True(s.Tank.Heat >= s.Tank.OffNetworkFloor - 1e-3f);
        Assert.True(s.Tank.OffNetworkFloor > 5f, $"the floor keeps climbing off network. floor={s.Tank.OffNetworkFloor:0.0}");
    }

    [Fact]
    public void AMomentInCoverage_CoolsSome_ButDoesNotResetTheClock()
    {
        var s = RelayHeatTests.CreateScenario();
        Park(s, AlongTunnel(s, 48), heat: 0f);
        Advance(s, Tweaks.Perf.TargetFps * 30);   // floor around 30
        float floorOut = s.Tank.OffNetworkFloor;

        s.Tank.Position = AlongTunnel(s, 6);       // dip into coverage for half a second
        Advance(s, Tweaks.Perf.TargetFps / 2);
        float floorAfterDip = s.Tank.OffNetworkFloor;

        // Coverage cools the tank (network plus its own passive shedding), and the floor follows the heat down, but
        // half a second is far from a reset to the coolant level.
        Assert.True(floorOut > 25f, $"floor={floorOut:0.0}");
        Assert.True(floorAfterDip < floorOut, "coverage should cool the tank a little");
        Assert.True(floorAfterDip > floorOut * 0.5f, $"a dip must not reset the clock. floor {floorOut:0.0} -> {floorAfterDip:0.0}");
    }

    [Fact]
    public void ARelay_ExtendsCoverage_AtOneHopsStrength()
    {
        var s = RelayHeatTests.CreateScenario();
        RelayHeatTests.PlantRelay(s);   // 30 beyond the door
        Park(s, AlongTunnel(s, 45), heat: 20f);

        Advance(s, Tweaks.Perf.TargetFps);

        Assert.True(s.Tank.Coverage.Covered, "the relay should cover the far end of the tunnel");
        Assert.Equal(NetworkCoverage.StrengthForHops(1), s.Tank.Coverage.Strength, precision: 5);
        Assert.Equal(s.RelayPos, s.Tank.Coverage.Node);
        Assert.True(s.Tank.Heat < 20f, $"and cool the tank there. heat={s.Tank.Heat:0.0}");
    }

    [Fact]
    public void Dirt_BetweenNodeAndTank_BlocksCoverage()
    {
        var s = RelayHeatTests.CreateScenario();
        RelayHeatTests.PlantRelay(s);
        Advance(s, 12);
        var tankAt = AlongTunnel(s, 45);
        // A dirt plug across the tunnel between the relay (30) and the tank (45).
        for (int dx = -2; dx <= 2; dx++)
            s.World.Terrain.SetPixel(AlongTunnel(s, 38) + new Offset(dx, 0), TerrainPixel.Dirt);
        Park(s, tankAt, heat: 0f);

        Advance(s, Tweaks.Perf.TargetFps);

        Assert.True(s.Tank.IsOffNetwork, "coverage needs line of sight, like links");
    }

    [Fact]
    public void Strength_FallsWithEveryHop()
    {
        Assert.Equal(1f, NetworkCoverage.StrengthForHops(0));
        Assert.True(NetworkCoverage.StrengthForHops(1) < 1f);
        Assert.True(NetworkCoverage.StrengthForHops(5) < NetworkCoverage.StrengthForHops(1));
    }

    [Fact]
    public void CoverageCoolingAndOffNetworkHeat_BalanceWithTheLedger()
    {
        var s = RelayHeatTests.CreateScenario();
        RelayHeatTests.PlantRelay(s);
        Park(s, AlongTunnel(s, 45), heat: 40f);
        var ledger = s.World.Terrain.Ledger;
        double start = s.World.SumSystemThermalEnergy() + s.World.SumSystemCharge();
        double startExternal = ledger.Injected - ledger.TotalSunk;
        double maxResidual = 0.0;

        for (int i = 0; i < Tweaks.Perf.TargetFps * 6; i++)
        {
            // Halfway through, drive the tank out of coverage and back.
            var input = i is > 48 and < 72 ? new Tunnerer.Core.Input.ControllerOutput { MoveSpeed = new Offset(0, s.DirY) } : default;
            s.World.Advance(t => t == 0 ? input : default);
            double now = s.World.SumSystemThermalEnergy() + s.World.SumSystemCharge();
            maxResidual = Math.Max(maxResidual, Math.Abs((now - start) - (ledger.Injected - ledger.TotalSunk - startExternal)));
        }

        Assert.True(maxResidual < 1.0, $"coverage must not create or lose energy unbooked. residual={maxResidual:0.0000}");
    }
}
