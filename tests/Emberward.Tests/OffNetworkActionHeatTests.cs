using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Input;
using Emberward.Core.Terrain;
using Emberward.Core.Thermal;
using Emberward.Core.Types;
using static Emberward.Tests.TankHeatTestHelpers;

namespace Emberward.Tests;

/// <summary>
/// DESIGN.md §4.6: off the network a tank must be able to heat itself up with no emberite, by shooting and torching
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
    public void OffNetwork_ShootingAndTorching_HeatTheTankAboveTheIdleFloor_AndTheLedgerBalances()
    {
        var idle = RunOffNetwork(shootAndTorch: false, out _, out _);
        var busy = RunOffNetwork(shootAndTorch: true, out int shots, out int dug);

        Assert.True(shots >= 5 && dug >= 20, $"the run must shoot and torch. shots={shots} dug={dug}");
        float actionHeat = shots * Tweaks.Tank.HeatShootPerShot + dug * Tweaks.Tank.HeatDigPerPixel;
        // The tank sheds a little into the ground meanwhile, so allow for that; the heat must still be there.
        Assert.True(busy.Heat - idle.Heat > 0.6f * actionHeat,
            $"action heat must show on a tank with no ore. rise={busy.Heat - idle.Heat:0.000} action={actionHeat:0.000}");
        Assert.True(busy.IsOffNetwork);
        Assert.True(busy.Heat - busy.OffNetworkFloor > 0.5f * actionHeat, "heat above the floor is not clamped away");
    }

    private static Emberward.Core.Entities.Tanks.Tank RunOffNetwork(bool shootAndTorch, out int shots, out int dug)
    {
        var s = RelayHeatTests.CreateScenario();
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
        var input = shootAndTorch
            ? new ControllerOutput { MoveSpeed = new Offset(0, dirY), ShootPrimary = true, AimDirection = new DirectionF(0, dirY) }
            : default;
        shots = 0;
        dug = 0;
        for (int i = 0; i < 90; i++)
        {
            world.Advance(_ => input);
            foreach (var e in world.StepEvents)
            {
                if (e.Kind == StepEventKind.ShotFired) shots++;
                if (e.Kind == StepEventKind.TankDug && e.Torch) dug += e.Count;
            }
            Assert.Equal(open, world.SumSystemThermalEnergy() - ledger.Injected + ledger.TotalSunk, tolerance: 1.0);
        }
        return tank;
    }
}
