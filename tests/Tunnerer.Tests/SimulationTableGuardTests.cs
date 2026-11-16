using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Thermal;

namespace Tunnerer.Tests;

/// <summary>
/// Fail loudly when someone adds an enum value but forgets one of the tables that has to know about it.
/// Each test names the place to update.
/// </summary>
public class SimulationTableGuardTests
{
    [Fact]
    public void ThermalMaterial_ValuesAreContiguousFromZero()
    {
        // ThermalMaterialTable and TerrainHeatEngine index their tables by (int)ThermalMaterial.
        int[] values = Enum.GetValues<ThermalMaterial>().Select(m => (int)m).Order().ToArray();
        Assert.Equal(Enumerable.Range(0, values.Length), values);
    }

    [Fact]
    public void EveryThermalMaterial_HasARowInTheMaterialTable()
    {
        // A failure here means Tweaks.World.ThermalMaterials has no usable row for a new ThermalMaterial
        // (the table's constructor already throws when a row is missing or doubled).
        var table = Tweaks.World.ThermalMaterials;
        Assert.Equal(Enum.GetValues<ThermalMaterial>().Length, ThermalMaterialTable.Count);
        foreach (ThermalMaterial material in Enum.GetValues<ThermalMaterial>())
        {
            var row = table[material];
            Assert.True(row.HeatCapacity > 0f, $"{material} has no positive heat capacity");
            Assert.True(row.Conductivity > 0f, $"{material} has no positive conductivity");
            Assert.True(row.AirExchange >= 0f, $"{material} has a negative air exchange");
        }
    }

    [Fact]
    public void MaterialTable_RejectsAMissingRow()
    {
        var rows = Enum.GetValues<ThermalMaterial>()
            .Where(m => m != ThermalMaterial.HotRock)
            .Select(m => (m, Tweaks.World.ThermalMaterials[m]));
        Assert.Throws<ArgumentException>(() => new ThermalMaterialTable(rows));
    }

    [Fact]
    public void PairConductance_IsTheTwoConductivitiesInSeries_UnlessOverridden()
    {
        var table = Tweaks.World.ThermalMaterials;
        foreach (ThermalMaterial a in Enum.GetValues<ThermalMaterial>())
        {
            foreach (ThermalMaterial b in Enum.GetValues<ThermalMaterial>())
            {
                float ka = table[a].Conductivity, kb = table[b].Conductivity;
                float expected = a == b ? ka * 0.5f : ka * kb / (ka + kb);
                foreach (var o in table.Overrides)
                {
                    if ((o.A, o.B) == (a, b) || (o.A, o.B) == (b, a))
                        expected = o.Conductance;
                }

                Assert.Equal(expected, table.PairConductance(a, b));
                Assert.Equal(table.PairConductance(a, b), table.PairConductance(b, a));
            }
        }
    }

    [Fact]
    public void MaterialTable_RejectsADoubledOrNonPositiveOverride()
    {
        var rows = Enum.GetValues<ThermalMaterial>().Select(m => (m, Tweaks.World.ThermalMaterials[m])).ToArray();
        Assert.Throws<ArgumentException>(() => new ThermalMaterialTable(rows,
            [new(ThermalMaterial.Dirt, ThermalMaterial.Stone, 0.1f), new(ThermalMaterial.Stone, ThermalMaterial.Dirt, 0.2f)]));
        Assert.Throws<ArgumentException>(() => new ThermalMaterialTable(rows,
            [new(ThermalMaterial.Dirt, ThermalMaterial.Stone, 0f)]));
    }

    [Fact]
    public void MaterialTable_ReproducesTheTunedConductances()
    {
        // The table replaced hand-tuned pairwise constants and reproduces every one that can carry heat. These are
        // those values (hot and frost rock conduct like stone); terrain-air exchange is the AirExchange column, and air-air
        // the air row's own pair. Base-base, base-core and core-core are left to the rule: both cells are pinned to
        // the same temperature, so whatever flows between them is pinned away in the same step.
        const ThermalMaterial D = ThermalMaterial.Dirt, S = ThermalMaterial.Stone, B = ThermalMaterial.Base,
            E = ThermalMaterial.ConstantEnergy, H = ThermalMaterial.HotRock, F = ThermalMaterial.FrostRock,
            A = ThermalMaterial.Air;
        (ThermalMaterial, ThermalMaterial, float)[] tuned =
        [
            (A, A, 0.240f), (D, D, 0.045f), (D, S, 0.070f), (D, B, 0.090f), (D, E, 0.100f), (D, H, 0.070f),
            (S, S, 0.300f), (S, B, 0.450f), (S, E, 0.450f), (S, H, 0.300f), (H, H, 0.300f), (H, B, 0.450f),
            (H, E, 0.450f), (D, F, 0.070f), (S, F, 0.300f), (F, F, 0.300f), (F, H, 0.300f), (F, B, 0.450f),
            (F, E, 0.450f),
        ];
        var table = Tweaks.World.ThermalMaterials;
        var wrong = tuned
            .Where(p => p.Item3 != table.PairConductance(p.Item1, p.Item2))
            .Select(p => $"{p.Item1}-{p.Item2}: expected {p.Item3:R}, got {table.PairConductance(p.Item1, p.Item2):R}");
        Assert.Empty(wrong);
        float[] airExchange = [0f, 0.035f, 0.220f, 0.450f, 0.550f, 0.220f, 0.220f];
        foreach (ThermalMaterial m in Enum.GetValues<ThermalMaterial>())
            Assert.Equal(airExchange[(int)m], table[m].AirExchange);
    }

    [Fact]
    public void Engine_ReadsTheMaterialTableFromSettings()
    {
        // Settings stay injectable: a test can swap one row and the engine uses it.
        var settings = SimulationSettings.FromTweaks();
        var dirt = settings.ThermalMaterials[ThermalMaterial.Dirt] with { HeatCapacity = 9f };
        var engine = new TerrainHeatEngine(settings with { ThermalMaterials = settings.ThermalMaterials.With(ThermalMaterial.Dirt, dirt) });
        Assert.Equal(9f, engine.GetHeatCapacity(ThermalMaterial.Dirt));
        Assert.Equal(Tweaks.World.ThermalMaterials[ThermalMaterial.Stone].HeatCapacity, engine.GetHeatCapacity(ThermalMaterial.Stone));
    }

    [Fact]
    public void TerrainPixels_AreExactlyTheReviewedSet()
    {
        // Pixel.DeriveThermalMaterial maps any pixel it does not name to Air, so a new TerrainPixel would silently
        // become air. This list is the reviewed set: when you add a pixel, decide its thermal material in
        // DeriveThermalMaterial and add its name here. A failure here means exactly that.
        string[] reviewed =
        {
            "LevelGenDirt", "LevelGenRock", "LevelGenMark", "Blank", "Dirt", "DirtGrow", "Rock", "Decal",
            "BaseMin", "BaseMax", "BaseBarrier", "BaseCore", "HotRock", "FrostRock", "Concrete", "Gold",
        };
        string[] actual = Enum.GetNames<TerrainPixel>();
        Assert.Equal(reviewed.Order(), actual.Order());
    }

    [Theory]
    [InlineData(TerrainPixel.HotRock)]
    [InlineData(TerrainPixel.FrostRock)]
    [InlineData(TerrainPixel.BaseBarrier)]
    [InlineData(TerrainPixel.BaseCore)]
    public void FixedTemperatureCells_StayPinned_AndTheLedgerAccountsForIt(TerrainPixel pinned)
    {
        // A hot neighbour pushes heat into the pinned cell; the solver must put it back and meter the difference.
        var engine = new TerrainHeatEngine();
        ThermalMaterial material = Pixel.GetThermalMaterial(pinned);
        Assert.True(engine.IsFixedTemperatureMaterial(material));
        float fixedTemperature = engine.GetFixedTemperature(material);

        var pixels = new[] { TerrainPixel.Rock, pinned, TerrainPixel.Rock };
        var terrain = new[] { 500f, fixedTemperature, 500f };
        var air = new float[3];
        var ledger = new ThermalLedger();
        double start = engine.SumTotalEnergy(terrain, air, pixels);

        for (int step = 0; step < 50; step++)
            FullGridHeatStep.Step(engine, terrain, air, pixels, width: 3, height: 1, ledger);

        Assert.Equal(fixedTemperature, terrain[1]);
        double now = engine.SumTotalEnergy(terrain, air, pixels);
        Assert.Equal(start - ledger.SunkFixedTemperature, now, tolerance: 0.05);
    }

    [Fact]
    public void EveryMachineType_HasABehavior()
    {
        // A failure here means MachineBehaviors.Get has no entry for a new MachineType.
        foreach (MachineType type in Enum.GetValues<MachineType>())
            Assert.NotNull(MachineBehaviors.Get(type));
    }
}
