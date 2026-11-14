using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Input;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Thermal;
using Tunnerer.Core.Types;
using Xunit.Abstractions;
using static Tunnerer.Tests.TankHeatTestHelpers;

namespace Tunnerer.Tests;

/// <summary>
/// Energy-conservation invariants for the thermal model.
/// Everything inside the system (terrain field, air field, tank heat, base reactor) may only move energy
/// around; whatever is created or destroyed has to be recorded in <see cref="ThermalLedger"/>, so
/// <c>E(now) - E(start) == NetExternal(now) - NetExternal(start)</c> holds at every tick.
/// </summary>
public class ThermalLedgerTests(ITestOutputHelper output)
{
    private static TerrainGrid MakeMixedGrid(int w, int h)
    {
        var t = new TerrainGrid(new Size(w, h));
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                TerrainPixel pixel = TerrainPixel.Blank;
                if (x < w / 3) pixel = TerrainPixel.DirtHigh;
                else if (x == w / 2) pixel = TerrainPixel.Rock;
                t.SetPixelRaw(new Position(x, y), pixel);
            }

        return t;
    }

    [Fact]
    public void TankHeatEngine_ExchangeWithGrid_ConservesEnergy()
    {
        // Tank sits across dirt, rock and open air so every material capacity is exercised.
        var t = MakeMixedGrid(32, 32);
        var pos = new Position(32 / 3 - 1, 16);
        int radius = Tweaks.Tank.DigRadius;
        t.AddHeatRadius(pos, amount: 200, radius: 6);
        t.AddAirHeatTotalInRadiusArea(pos, 6, 800);

        var engine = new TankHeatEngine();
        float tankHeat = 90f;
        double tankCapacity = Tweaks.Tank.TankHeatCapacity;
        double maxResidual = 0.0;

        for (int i = 0; i < 400; i++)
        {
            double before = t.SumTotalThermalEnergy() + tankHeat * tankCapacity;

            var step = engine.Advance(
                tankHeat,
                frameDugPixels: 0,
                frameShotFired: false,
                sampledTerrainTemperature: t.SampleAverageHeat(pos, radius) * 255f,
                sampledAirTemperature: t.SampleAverageAirTemperature(pos, radius),
                terrainCapacity: t.SumHeatCapacityInRadiusArea(pos, radius, TerrainGrid.ThermalField.Terrain),
                airCapacity: t.SumHeatCapacityInRadiusArea(pos, radius, TerrainGrid.ThermalField.Air));
            t.AddEnergyInRadiusArea(pos, radius, step.EnergyToTerrain, TerrainGrid.ThermalField.Terrain);
            t.AddEnergyInRadiusArea(pos, radius, step.EnergyToAir, TerrainGrid.ThermalField.Air);
            tankHeat = step.NextHeat;

            double after = t.SumTotalThermalEnergy() + tankHeat * tankCapacity;
            maxResidual = Math.Max(maxResidual, Math.Abs(after - before));
        }

        // The old coupling created ~49x the energy the tank lost; float noise here is orders of magnitude lower.
        Assert.True(maxResidual < 0.05, $"Tank/grid exchange must conserve energy. maxResidualPerStep={maxResidual:0.000000}");
        Assert.NotEqual(90f, tankHeat); // the tank really exchanged heat
    }

    [Theory]
    [InlineData(TerrainGrid.ThermalField.Terrain)]
    [InlineData(TerrainGrid.ThermalField.Air)]
    public void ExtractEnergy_TakesExactlyWhatIsAsked_AndNeverGoesBelowAmbient(TerrainGrid.ThermalField field)
    {
        var t = MakeMixedGrid(32, 32);
        var center = new Position(6, 16);
        if (field == TerrainGrid.ThermalField.Terrain)
            t.AddHeatRadius(center, amount: 40, radius: 5);
        else
            t.AddAirHeatTotalInRadiusArea(center, 5, 400);

        double before = t.SumTotalThermalEnergy();
        double taken = t.ExtractEnergyInRadiusArea(center, radius: 8, maxEnergy: 5.0, field);
        double after = t.SumTotalThermalEnergy();

        Assert.Equal(5.0, taken, precision: 6);
        Assert.Equal(before - 5.0, after, precision: 2);

        // Asking for far more than exists drains the heat but cannot push anything below ambient.
        double drained = t.ExtractEnergyInRadiusArea(center, radius: 8, maxEnergy: 1e9, field);
        Assert.True(drained > 0.0);
        for (int dy = -8; dy <= 8; dy++)
            for (int dx = -8; dx <= 8; dx++)
            {
                var p = new Position(center.X + dx, center.Y + dy);
                float temperature = field == TerrainGrid.ThermalField.Terrain
                    ? t.GetHeatTemperature(p)
                    : t.GetAirTemperature(p);
                Assert.True(temperature >= -1e-3f, $"cell {p} dropped below ambient: {temperature}");
            }

        Assert.Equal(0.0, t.ExtractEnergyInRadiusArea(center, radius: 8, maxEnergy: 10.0, field), precision: 3);
    }

    [Fact]
    public void Grid_WithFixedTemperatureCells_EnergyBalancesWithLedger()
    {
        // A hot spot next to pinned base cells: the pins delete energy, and the ledger must say how much.
        var t = MakeMixedGrid(48, 48);
        for (int y = 20; y < 30; y++)
            for (int x = 30; x < 40; x++)
                t.SetPixelRaw(new Position(x, y), TerrainPixel.BaseCore);
        for (int x = 30; x < 40; x++)
            t.SetPixelRaw(new Position(x, 19), TerrainPixel.BaseBarrier);

        t.AddHeatRadius(new Position(26, 24), amount: 220, radius: 9);
        t.AddAirHeatTotalInRadiusArea(new Position(26, 24), 9, 600);

        double startEnergy = t.SumTotalThermalEnergy();
        double startNet = t.Ledger.NetExternal;
        double maxResidual = 0.0;
        for (int i = 0; i < 400; i++)
        {
            t.CoolDown();
            double residual = (t.SumTotalThermalEnergy() - startEnergy) - (t.Ledger.NetExternal - startNet);
            maxResidual = Math.Max(maxResidual, Math.Abs(residual));
        }

        output.WriteLine($"sunkFixed={t.Ledger.SunkFixedTemperature:0.0} sunkMirror={t.Ledger.SunkAirMirror:0.0} maxResidual={maxResidual:0.0000}");
        Assert.True(t.Ledger.SunkFixedTemperature > 0.0, "the pinned cells should have absorbed heat");
        Assert.True(maxResidual < 0.5, $"Grid energy must balance with the ledger. maxResidual={maxResidual:0.0000}");
    }

    [Fact]
    public void World_RandomPlay_EnergyBalancesWithLedger()
    {
        // Two tanks drive, dig, shoot and die at random. Regrowth is off because it uses an unseeded RNG
        // and is irrelevant to the balance (its material changes are metered like every other pixel write).
        var world = TestHelpers.CreateSeededWorld(seed: 7321, enableTerrainRegrowth: false);
        var random = new Random(1234);
        var tanks = world.TankList.Tanks;

        double startEnergy = SumSystemThermalEnergy(world);
        double startNet = world.Terrain.Ledger.NetExternal;
        double maxResidual = 0.0;
        double maxEnergy = startEnergy;
        int worstTick = -1;

        ControllerOutput RandomInput()
        {
            var aim = new DirectionF((float)(random.NextDouble() * 2 - 1), (float)(random.NextDouble() * 2 - 1));
            return new ControllerOutput
            {
                MoveSpeed = new Offset(random.Next(-1, 2), random.Next(-1, 2)),
                AimDirection = aim,
                ShootPrimary = random.NextDouble() < 0.35,
            };
        }

        const int ticks = 1500;
        for (int i = 0; i < ticks; i++)
        {
            // Draw one input per tank per tick up front so the sequence is independent of call order.
            var inputs = new ControllerOutput[tanks.Count];
            for (int k = 0; k < inputs.Length; k++)
                inputs[k] = RandomInput();

            world.Advance(idx => inputs[idx]);

            double energy = SumSystemThermalEnergy(world);
            maxEnergy = Math.Max(maxEnergy, energy);
            double residual = (energy - startEnergy) - (world.Terrain.Ledger.NetExternal - startNet);
            if (Math.Abs(residual) > maxResidual)
            {
                maxResidual = Math.Abs(residual);
                worstTick = i;
            }
        }

        var ledger = world.Terrain.Ledger;
        output.WriteLine(
            $"peakEnergy={maxEnergy:0.0} injected={ledger.Injected:0.0} sunkFixed={ledger.SunkFixedTemperature:0.0} " +
            $"sunkMirror={ledger.SunkAirMirror:0.0} sunkBase={ledger.SunkBase:0.0} sunkTank={ledger.SunkTank:0.0} " +
            $"sunkArtificial={ledger.SunkArtificialCooling:0.0} maxResidual={maxResidual:0.0000} at tick {worstTick}");

        Assert.True(ledger.Injected > 0.0, "the scenario should actually inject heat (shots, torch, explosions)");
        Assert.True(maxResidual < 0.5,
            $"Energy created or destroyed outside the ledger. maxResidual={maxResidual:0.0000} at tick {worstTick}");
    }
}
