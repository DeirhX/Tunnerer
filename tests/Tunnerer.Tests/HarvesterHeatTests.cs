using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

namespace Tunnerer.Tests;

/// <summary>
/// Working machines give off heat: a harvester that digs dumps energy into the ground beside it.
/// </summary>
public class HarvesterHeatTests
{
    [Fact]
    public void Harvester_WhenItHarvests_AddsHeatToTheGround()
    {
        var working = RelayHeatTests.CreateScenario();
        var idle = RelayHeatTests.CreateScenario();

        var harvester = new Machine(working.RelayPos, MachineType.Harvester, working.Tank.Color);
        working.World.Machines.Add(harvester);
        working.World.PlantMachine(harvester, null);

        double startWorking = working.World.SumSystemThermalEnergy();
        double startIdle = idle.World.SumSystemThermalEnergy();

        // One harvest interval plus a little slack, so at least one harvest happens.
        int ticks = (int)Math.Ceiling(Tweaks.Machine.HarvesterIntervalMs / 1000.0 * Tweaks.Perf.TargetFps) + 2;
        for (int i = 0; i < ticks; i++)
        {
            working.World.Advance(_ => default);
            idle.World.Advance(_ => default);
        }

        double extra = (working.World.SumSystemThermalEnergy() - startWorking) - (idle.World.SumSystemThermalEnergy() - startIdle);
        Assert.True(extra > Tweaks.Machine.HarvesterHeatPerHarvest * 0.5,
            $"a harvest should leave real heat in the ground. extra={extra:0.0}");
    }

    private static int DirtAround(RelayHeatTests.Scenario s, int range)
    {
        int count = 0;
        var t = s.World.Terrain;
        for (int dy = -range; dy <= range; dy++)
            for (int dx = -range; dx <= range; dx++)
            {
                var pos = s.RelayPos + new Offset(dx, dy);
                if (dx * dx + dy * dy <= range * range && t.IsInside(pos) && Pixel.IsDirt(t.GetPixelRaw(pos)))
                    count++;
            }
        return count;
    }

    /// <summary>Runs a harvester for a minute of game time and returns (dirt dug, heat left in the ground).</summary>
    private static (int Dug, double HeatLeft) RunHarvester(bool withRelay)
    {
        var s = RelayHeatTests.CreateScenario();
        var harvester = new Machine(s.RelayPos, MachineType.Harvester, s.Tank.Color);
        s.World.Machines.Add(harvester);
        s.World.PlantMachine(harvester, s.Base);
        if (withRelay)
            RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(0, s.DirY * 3));

        int dirtBefore = DirtAround(s, Tweaks.Machine.HarvestRange);
        double heatBefore = s.World.SumSystemThermalEnergy();
        for (int i = 0; i < Tweaks.Perf.TargetFps * 60; i++)
            s.World.Advance(_ => default);

        return (dirtBefore - DirtAround(s, Tweaks.Machine.HarvestRange), s.World.SumSystemThermalEnergy() - heatBefore);
    }

    [Fact]
    public void Harvester_WithoutACoolingPath_StallsAndKeepsItsHeatBounded()
    {
        var (dug, heatLeft) = RunHarvester(withRelay: false);

        double unthrottledDug = 60.0 * 1000.0 / Tweaks.Machine.HarvesterIntervalMs;
        Assert.True(dug > 0, "it should still dig until the ground gets hot");
        Assert.True(dug < unthrottledDug * 0.7, $"a harvester with nowhere to put its heat must slow down. dug={dug}, unthrottled={unthrottledDug:0}");
        Assert.True(heatLeft < dug * Tweaks.Machine.HarvesterHeatPerHarvest + 1.0, "heat in the ground never exceeds what the digging made");
    }

    [Fact]
    public void Harvester_WithALinkedRelay_KeepsWorkingBecauseTheHeatLeaves()
    {
        var alone = RunHarvester(withRelay: false);
        var piped = RunHarvester(withRelay: true);

        Assert.True(piped.Dug > alone.Dug, $"a relay should let the harvester work faster. piped={piped.Dug}, alone={alone.Dug}");
        Assert.True(piped.HeatLeft < alone.HeatLeft, $"a relay should leave less heat in the ground. piped={piped.HeatLeft:0}, alone={alone.HeatLeft:0}");
    }

    [Fact]
    public void Harvester_Heat_IsBookedInTheLedger_SoTheEnergyBalanceHolds()
    {
        var s = RelayHeatTests.CreateScenario();
        var harvester = new Machine(s.RelayPos, MachineType.Harvester, s.Tank.Color);
        s.World.Machines.Add(harvester);
        s.World.PlantMachine(harvester, s.Base);

        double start = s.World.SumSystemThermalEnergy();
        double startNet = s.World.Terrain.Ledger.NetExternal;
        double maxResidual = 0.0;
        for (int i = 0; i < Tweaks.Perf.TargetFps * 20; i++)
        {
            s.World.Advance(_ => default);
            double residual = (s.World.SumSystemThermalEnergy() - start) - (s.World.Terrain.Ledger.NetExternal - startNet);
            maxResidual = Math.Max(maxResidual, Math.Abs(residual));
        }

        Assert.True(s.World.Terrain.Ledger.Injected > 0, "harvesting should have injected heat");
        Assert.True(maxResidual < 0.5, $"harvest heat must be accounted for. residual={maxResidual:0.0000}");
    }

    [Fact]
    public void Harvester_OnHotGround_ReportsStalledInItsRenderState()
    {
        var s = RelayHeatTests.CreateScenario();
        var harvester = new Machine(s.RelayPos, MachineType.Harvester, s.Tank.Color);
        s.World.Machines.Add(harvester);
        s.World.PlantMachine(harvester, s.Base);
        s.World.Terrain.AddHeatRadius(s.RelayPos, 200, 6);

        int ticks = (int)Math.Ceiling(Tweaks.Machine.HarvesterIntervalMs / 1000.0 * Tweaks.Perf.TargetFps) + 2;
        for (int i = 0; i < ticks; i++)
            s.World.Advance(_ => default);

        var states = new Tunnerer.Core.Rendering.MachineRenderState[8];
        int count = s.World.Machines.CopyRenderStates(states);
        Assert.True(harvester.IsStalled, "a harvester on very hot ground should be stalled");
        Assert.Contains(states.Take(count), m => m.Type == MachineType.Harvester && m.IsStalled);
    }

    [Fact]
    public void SecondaryBuildKey_BuildsCarriesAndPlantsAHarvester()
    {
        var s = RelayHeatTests.CreateScenario();
        s.Tank.Resources.Add(new Tunnerer.Core.Resources.MaterialAmount(Tweaks.Machine.HarvesterDirtCost + 50, 0));
        s.Tank.Position = s.RelayPos;
        var world = s.World;
        int dirtBefore = s.Tank.Resources.Current.Dirt;

        void Press(bool down) => world.Advance(i => i == 0 ? new Tunnerer.Core.Input.ControllerOutput { BuildSecondary = down } : default);

        Press(true);
        Assert.True(s.Tank.IsCarryingMachine);
        Assert.Equal(MachineType.Harvester, s.Tank.CarriedMachineType);
        Assert.Equal(dirtBefore - Tweaks.Machine.HarvesterDirtCost, s.Tank.Resources.Current.Dirt);

        Press(false);
        Press(true); // a fresh press plants it where the tank stands

        Assert.False(s.Tank.IsCarryingMachine);
        var harvester = world.Machines.Machines.Single(m => m.Type == MachineType.Harvester);
        Assert.Equal(MachineState.Planted, harvester.State);
        Assert.Same(s.Base, harvester.OwnerBase);
    }
}
