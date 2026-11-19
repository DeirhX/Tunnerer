using Emberward.Core.Config;
using Emberward.Core.Input;
using Emberward.Core.Terrain;
using Emberward.Core.Thermal;
using Emberward.Core.Types;
using Xunit.Abstractions;
using static Emberward.Tests.TankHeatTestHelpers;

namespace Emberward.Tests;

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
                if (x < w / 3) pixel = TerrainPixel.Dirt;
                else if (x == w / 2) pixel = TerrainPixel.Rock;
                t.SetPixelRaw(new Position(x, y), pixel);
            }

        return t;
    }

    [Fact]
    public void DiggingHotDirtToAir_LeavesNoGhostHeatInTheTerrainField()
    {
        var t = new TerrainGrid(new Size(16, 16));
        t.Fill(TerrainPixel.Dirt);
        var pos = new Position(8, 8);
        t.AddHeatRadius(pos, amount: 200, radius: 2);
        Assert.True(t.SampleAverageHeat(pos, 0) > 0f, "the dirt must be hot before it is dug");

        double energyBefore = t.SumTotalThermalEnergy();
        double externalBefore = t.Ledger.NetExternal;
        t.SetPixel(pos, TerrainPixel.Blank);

        // The dirt's energy left the system with the dirt, and the ledger says so.
        double residual = (t.SumTotalThermalEnergy() - energyBefore) - (t.Ledger.NetExternal - externalBefore);
        Assert.InRange(residual, -1e-3, 1e-3);

        // The new air cell holds no heat, so nothing can be sampled or extracted from it in the same tick.
        Assert.Equal(0f, t.SampleAverageHeat(pos, 0));
        double taken = t.ExtractEnergyInRadiusArea(pos, 0, maxEnergy: 1e9, TerrainGrid.ThermalField.Terrain);
        Assert.Equal(0.0, taken);
        Assert.True(t.SampleTankContact(pos, 0).AirTemperature >= Tweaks.World.ThermalAmbientTemperature,
            "extracting from freshly dug air must not push it below ambient");
    }

    [Fact]
    public void TankHeatEngine_ContactExchange_ConservesEnergy()
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

            var contact = t.SampleTankContact(pos, radius);
            var step = engine.Advance(
                tankHeat,
                frameDugPixels: 0,
                frameShotFired: false,
                contact: contact);
            t.ApplyTankExchange(pos, radius, contact, step.EnergyToTerrain, step.EnergyToAir);
            tankHeat = step.NextHeat;

            double after = t.SumTotalThermalEnergy() + tankHeat * tankCapacity;
            maxResidual = Math.Max(maxResidual, Math.Abs(after - before));
        }

        // The old coupling created ~49x the energy the tank lost; float noise here is orders of magnitude lower.
        Assert.True(maxResidual < 0.05, $"Tank/grid exchange must conserve energy. maxResidualPerStep={maxResidual:0.000000}");
        Assert.NotEqual(90f, tankHeat); // the tank really exchanged heat
    }

    [Fact]
    public void TankConductsMoreThroughStoneThanAir_AndOpenCellsExchangeOnlyOnce()
    {
        var t = new TerrainGrid(new Size(5, 5));
        var pos = new Position(2, 2);
        var engine = new TankHeatEngine();
        t.Fill(TerrainPixel.Blank);
        var open = t.SampleTankContact(pos, 2);
        var inAir = engine.Advance(10, 0, false, open);
        Assert.Equal(0.0, inAir.EnergyToTerrain);
        Assert.Equal(10 * Tweaks.Tank.TankAmbientConductance, inAir.EnergyToAir, precision: 5);

        t.Fill(TerrainPixel.Rock);
        var inRock = engine.Advance(10, 0, false, t.SampleTankContact(pos, 2));
        Assert.Equal(10 * Tweaks.Tank.TankTerrainConductance, inRock.EnergyToTerrain, precision: 5);
        Assert.True(inRock.EnergyToTerrain > inAir.EnergyToAir);
    }

    [Theory]
    [InlineData(50f, -10f)]
    [InlineData(-10f, 50f)]
    public void SmallContactCanReachEquilibriumWithoutCrossingIt(float tankTemperature, float solidTemperature)
    {
        const double capacity = 0.1;
        var contact = new TankHeatContact(solidTemperature, capacity, 1f, 0f, 0.0);
        var engine = new TankHeatEngine();
        var step = engine.Advance(tankTemperature, 0, false, contact);
        double equilibrium = (tankTemperature * engine.TankHeatCapacity + solidTemperature * capacity)
            / (engine.TankHeatCapacity + capacity);
        Assert.Equal(equilibrium, step.NextHeat, precision: 4);
        Assert.Equal(equilibrium, solidTemperature + step.EnergyToTerrain / capacity, precision: 4);
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
    public void AirCell_InjectedHeat_IsKeptAndConserved()
    {
        // Open cave: no dirt, rock or base cells, and the air's own loss switched off, so nothing may remove heat.
        // Heat written into an air cell used to land in a mirror field that the next solver step overwrote,
        // deleting ~90% of it.
        var t = new TerrainGrid(new Size(40, 40), FullGridHeatStep.LosslessSettings());
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 40; x++)
                t.SetPixelRaw(new Position(x, y), TerrainPixel.Blank);

        var center = new Position(20, 20);
        t.AddHeat(center, 100);
        t.AddHeatRadius(new Position(10, 30), amount: 60, radius: 4);

        double injected = t.Ledger.Injected;
        Assert.Equal(injected, t.SumTotalThermalEnergy(), precision: 2);
        Assert.Equal(100f, t.GetAirTemperature(center), precision: 3);
        Assert.Equal(100f, t.GetHeatTemperature(center), precision: 3); // the terrain field mirrors the air

        for (int i = 0; i < 60; i++)
            t.CoolDown();

        Assert.Equal(injected, t.SumTotalThermalEnergy(), precision: 1);
        Assert.Equal(0.0, t.Ledger.TotalSunk, precision: 3);
        Assert.True(t.GetAirTemperature(center) < 100f, "the heat should spread out");
        Assert.True(t.GetAirTemperature(new Position(21, 20)) > 0f, "neighbours should warm up");
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

        output.WriteLine($"sunkFixed={t.Ledger.SunkFixedTemperature:0.0} maxResidual={maxResidual:0.0000}");
        Assert.True(t.Ledger.SunkFixedTemperature > 0.0, "the pinned cells should have absorbed heat");
        Assert.True(maxResidual < 0.5, $"Grid energy must balance with the ledger. maxResidual={maxResidual:0.0000}");
    }

    [Trait("Category", "Slow")]
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
            $"sunkBase={ledger.SunkBase:0.0} sunkTank={ledger.SunkTank:0.0} " +
            $"sunkLoss={ledger.SunkAmbientLoss:0.0} maxResidual={maxResidual:0.0000} at tick {worstTick}");

        Assert.True(ledger.Injected > 0.0, "the scenario should actually inject heat (shots, torch, explosions)");
        Assert.True(maxResidual < 0.5,
            $"Energy created or destroyed outside the ledger. maxResidual={maxResidual:0.0000} at tick {worstTick}");
    }
}
