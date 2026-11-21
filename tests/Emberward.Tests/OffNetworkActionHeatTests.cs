using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Input;
using Emberward.Core.Terrain;
using Emberward.Core.Thermal;
using Emberward.Core.Types;
using static Emberward.Tests.TankHeatTestHelpers;

namespace Emberward.Tests;

/// <summary>
/// DESIGN.md §4.6: off the network a tank must be able to heat itself up with no pyrite, by shooting and torching
/// too much. The floor rises on its own; action heat comes on top of it.
/// </summary>
public class OffNetworkActionHeatTests
{
    [Fact]
    public void ActionHeat_IsShotsTimesShotHeatPlusDugCellsTimesDigHeat()
    {
        var engine = new TankHeatEngine();
        var none = new TankHeatContact(0f, 0.0, 0f, 0f, 0.0);

        var step = engine.Advance(10f, frameDugPixels: 7, frameShotFired: true, contact: none);

        float expected = Tweaks.Tank.HeatShootPerShot + 7 * Tweaks.Tank.HeatDigPerPixel;
        Assert.Equal(expected, step.ActionHeat, precision: 5);
        Assert.Equal(10f + expected, step.NextHeat, precision: 5);
    }

    [Fact]
    public void OffNetwork_ShootingAndTorching_HeatsTheTank_AndTheLedgerBalances()
    {
        // The same run twice: once as the game plays it, and once with shot and dig heat set to zero. Both torch the same
        // cells into the same ground, so the difference in the tank's heat is the action heat it kept.
        var withHeat = RunOffNetwork(SimulationSettings.FromTweaks(), out int shots, out int dug);
        var withoutHeat = RunOffNetwork(
            SimulationSettings.FromTweaks() with { TankHeatShootPerShot = 0f, TankHeatDigPerPixel = 0f }, out _, out _);

        Assert.True(shots >= 5 && dug >= 20, $"the run must shoot and torch. shots={shots} dug={dug}");
        float actionHeat = shots * Tweaks.Tank.HeatShootPerShot + dug * Tweaks.Tank.HeatDigPerPixel;
        float kept = withHeat.Heat - withoutHeat.Heat;
        // Most of the action heat leaves again within the run: the tank conducts heat into the air and the ground
        // the whole time. A quarter stays (measured about 0.27 when this was written), and a zero action heat would
        // keep none, so this bound is what tells the two runs apart.
        Assert.True(kept > 0.15f * actionHeat,
            $"action heat must reach the tank. kept={kept:0.000} action={actionHeat:0.000}");
        Assert.True(kept < 1.1f * actionHeat,
            $"the tank must not gain more than its action heat. kept={kept:0.000} action={actionHeat:0.000}");
        Assert.True(withHeat.IsOffNetwork);
        Assert.Equal(0, withHeat.OreHeld);
    }

    private static Emberward.Core.Entities.Tanks.Tank RunOffNetwork(SimulationSettings settings, out int shots, out int dug)
    {
        var s = RelayHeatTests.CreateScenario(simulationSettings: settings);
        var world = s.World;
        var tank = s.Tank;
        int dirY = s.DirY;
        var start = s.Base.Position + new Offset(0, dirY * (Tweaks.Base.BaseSize / 2 + 1 + 45));
        // A block of plain dirt just beyond the tunnel's end for the torch to work through.
        for (int dy = 1; dy <= 40; dy++)
            for (int dx = -6; dx <= 6; dx++)
                world.Terrain.SetPixel(start + new Offset(dx, dirY * (3 + dy)), TerrainPixel.Dirt);
        tank.Position = start;
        SetTankHeat(tank, 10f);

        var ledger = world.Terrain.Ledger;
        double open = world.SumSystemThermalEnergy() - ledger.Injected + ledger.TotalSunk;
        var input = new ControllerOutput { MoveSpeed = new Offset(0, dirY), ShootPrimary = true, AimDirection = new DirectionF(0, dirY) };
        shots = 0;
        dug = 0;
        // Only the tank under test is driven, and only its own shots and digs are counted: the scenario has an enemy tank.
        for (int i = 0; i < 90; i++)
        {
            world.Advance(slot => slot == 0 ? input : default);
            foreach (var e in world.StepEvents)
            {
                if (e.Color != tank.Color) continue;
                if (e.Kind == StepEventKind.ShotFired) shots++;
                if (e.Kind == StepEventKind.TankDug && e.Torch) dug += e.Count;
            }
            Assert.Equal(open, world.SumSystemThermalEnergy() - ledger.Injected + ledger.TotalSunk, tolerance: 1.0);
        }
        return tank;
    }
}
