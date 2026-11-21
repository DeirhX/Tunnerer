using Emberward.Core;
using Emberward.Core.Input;
using Emberward.Core.Terrain;
using Emberward.Core.Types;
using Emberward.Core.Entities.Tanks;

namespace Emberward.Tests;

internal static class TankHeatTestHelpers
{
    internal static ControllerOutput IdleInput => default;

    internal static SimulationContext CreateContext(World world) => new()
    {
        Terrain = world.Terrain,
        Heat = world.Terrain,
        Projectiles = world.Projectiles,
        CheckBaseCollision = world.TankBases.CheckBaseCollision,
        Settings = world.Settings,
        Machines = world.Machines,
        PlantMachine = world.PlantMachine,
    };

    internal static Position GetWorldCenter(World world) =>
        new(world.Terrain.Width / 2, world.Terrain.Height / 2);

    internal static Position RequireBasePosition(Tank tank)
    {
        Assert.NotNull(tank.Base);
        return tank.Base!.Position;
    }

    internal static void SetTankHeat(Tank tank, float heat)
    {
        tank.Heat = heat;
        tank.Reactor.Current.Heat = new Core.Resources.Heat((int)MathF.Round(heat));
    }

    /// <summary>
    /// Up (-1) or down (+1), whichever leaves <paramref name="basePos"/> toward the farther top or bottom edge of the map:
    /// a base backs onto a border, so a tunnel out through a door must run away from it.
    /// </summary>
    internal static int DirYIntoMap(TerrainGrid terrain, Position basePos) =>
        (terrain.Height - basePos.Y) >= basePos.Y ? 1 : -1;

    /// <summary>Clears the cells from <paramref name="startDy"/> to <paramref name="endDyInclusive"/> below <paramref name="anchor"/>, either way up.</summary>
    internal static void CarveVerticalLine(TerrainGrid terrain, Position anchor, int startDy, int endDyInclusive)
    {
        for (int i = Math.Min(startDy, endDyInclusive); i <= Math.Max(startDy, endDyInclusive); i++)
        {
            var pos = anchor + new Offset(0, i);
            if (terrain.IsInside(pos))
                terrain.SetPixel(pos, TerrainPixel.Blank);
        }
    }

    internal static void AdvanceTank(
        Tank tank,
        World world,
        int frames,
        TestVisualTrace? visual = null,
        string phase = "advance",
        int captureEvery = 0,
        Action<int>? beforeStep = null,
        Action<int>? afterStep = null)
    {
        var ctx = CreateContext(world);
        for (int i = 0; i < frames; i++)
        {
            beforeStep?.Invoke(i);
            tank.Advance(in ctx, IdleInput);
            if (captureEvery > 0 && (i % captureEvery) == 0)
                visual?.Capture(world, $"{phase}_{i:D3}");
            afterStep?.Invoke(i);
        }
    }

    internal static void ClearArea(TerrainGrid terrain, Position center, int radius)
    {
        for (int y = center.Y - radius; y <= center.Y + radius; y++)
            for (int x = center.X - radius; x <= center.X + radius; x++)
            {
                var pos = new Position(x, y);
                if (!terrain.IsInside(pos))
                    continue;
                terrain.SetPixel(pos, TerrainPixel.Blank);
            }
    }

    internal static void CoolAreaToZero(TerrainGrid terrain, Position center, int radius)
    {
        for (int y = center.Y - radius; y <= center.Y + radius; y++)
            for (int x = center.X - radius; x <= center.X + radius; x++)
            {
                var pos = new Position(x, y);
                if (!terrain.IsInside(pos))
                    continue;
                // Exactly ambient: a tank now exchanges with sub-zero cells too, so overshooting would freeze it.
                // The two fields share the same temperature in an open cell, which only stores energy in its air.
                if (Pixel.GetThermalMaterial(terrain.GetPixelRaw(pos)) != ThermalMaterial.Air)
                    removeHeat(pos, terrain.GetHeatTemperature(pos), TerrainGrid.ThermalField.Terrain);
                removeHeat(pos, terrain.GetAirTemperature(pos), TerrainGrid.ThermalField.Air);
            }

        void removeHeat(Position pos, float temperature, TerrainGrid.ThermalField field)
        {
            double energy = -temperature * terrain.SumHeatCapacityInRadiusArea(pos, radius: 0, field);
            terrain.Ledger.RecordInjected(terrain.AddEnergyInRadiusArea(pos, radius: 0, energy, field));
        }
    }

    internal static void SetupHotZoneScenario(World world, Tank tank, Position zonePos, float initialHeat)
    {
        tank.Position = zonePos;
        ClearArea(world.Terrain, zonePos, radius: 6);
        world.Terrain.AddHeatRadius(zonePos, amount: 255, radius: 6);
        SetTankHeat(tank, initialHeat);
    }

    internal static double SumSystemThermalEnergy(World world) => world.SumSystemThermalEnergy();

    internal static void AdvanceUntil(World world, int maxFrames, Func<bool> done, TestVisualTrace? visual = null, string phase = "advance")
    {
        for (int i = 0; i < maxFrames; i++)
        {
            if (done())
                return;
            world.Advance(_ => default);
            visual?.Capture(world, $"{phase}_{i:D4}");
        }
    }

    internal static float StdDev(IEnumerable<float> values, float mean)
    {
        double sumSq = 0.0;
        int n = 0;
        foreach (float v in values)
        {
            double d = v - mean;
            sumSq += d * d;
            n++;
        }

        return n > 0 ? (float)Math.Sqrt(sumSq / n) : 0f;
    }
}
