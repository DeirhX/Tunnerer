using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Machines;
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
}
