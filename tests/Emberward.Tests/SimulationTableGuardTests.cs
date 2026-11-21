using Emberward.Core.Config;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Terrain;
using Emberward.Core.Thermal;

namespace Emberward.Tests;

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
            Assert.False(row.IsFixedTemperature, $"{material} must have a finite heat stock");
            Assert.True(row.Loss > 0f, $"{material} has no loss to ambient");
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
        // those values (hot and frost rock conduct like stone). Finite base cells now use the same series rule.
        const ThermalMaterial D = ThermalMaterial.Dirt, S = ThermalMaterial.Stone, B = ThermalMaterial.Base,
            E = ThermalMaterial.ConstantEnergy, H = ThermalMaterial.HotRock, F = ThermalMaterial.FrostRock,
            A = ThermalMaterial.Air, O = ThermalMaterial.PyriteCore;
        (ThermalMaterial, ThermalMaterial, float)[] tuned =
        [
            (A, A, 0.240f), (D, D, 0.010f),
            (S, S, 0.300f), (S, B, 0.450f), (S, E, 0.450f), (S, H, 0.300f), (H, H, 0.300f), (H, B, 0.450f),
            (B, B, 0.900f), (B, E, 0.900f), (E, E, 0.900f),
            (H, E, 0.450f), (S, F, 0.300f), (F, F, 0.300f), (F, H, 0.300f), (F, B, 0.450f),
            (F, E, 0.450f), (O, O, 0.300f), (O, S, 0.300f), (O, B, 0.450f), (O, E, 0.450f),
        ];
        var table = Tweaks.World.ThermalMaterials;
        var wrong = tuned
            .Where(p => p.Item3 != table.PairConductance(p.Item1, p.Item2))
            .Select(p => $"{p.Item1}-{p.Item2}: expected {p.Item3:R}, got {table.PairConductance(p.Item1, p.Item2):R}");
        Assert.Empty(wrong);
        float[] airExchange = [0f, 0.035f, 0.220f, 0.450f, 0.550f, 0.220f, 0.220f, 0.220f];
        foreach (ThermalMaterial m in Enum.GetValues<ThermalMaterial>())
            Assert.Equal(airExchange[(int)m], table[m].AirExchange);
        // Hot rock gives up its stock to ambient more slowly than ordinary ground.
        float[] loss = [0.0010f, 0.0003f, 0.0003f, 0.0003f, 0.0003f, 0.00005f, 0.0003f, 0.0003f];
        foreach (ThermalMaterial m in Enum.GetValues<ThermalMaterial>())
            Assert.Equal(loss[(int)m], table[m].Loss);
    }

    [Fact]
    public void MaterialTable_RejectsALossOnAPinnedMaterialOrOutOfRange()
    {
        var table = Tweaks.World.ThermalMaterials;
        Assert.Throws<ArgumentException>(() =>
            table.With(ThermalMaterial.HotRock, table[ThermalMaterial.HotRock] with { FixedTemperature = 100f }));
        Assert.Throws<ArgumentException>(() =>
            table.With(ThermalMaterial.Dirt, table[ThermalMaterial.Dirt] with { Loss = 1f }));
        Assert.Throws<ArgumentException>(() =>
            table.With(ThermalMaterial.Dirt, table[ThermalMaterial.Dirt] with { Loss = -0.1f }));
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
            "BaseMin", "BaseMax", "BaseBarrier", "BaseFloor", "BaseCore", "BaseGateOpen", "HotRock", "FrostRock", "Concrete",
            "Pyrite", "PyriteCore",
        };
        string[] actual = Enum.GetNames<TerrainPixel>();
        Assert.Equal(reviewed.Order(), actual.Order());
    }

    [Fact]
    public void CustomFixedTemperature_StaysPinned_AndTheLedgerAccountsForIt()
    {
        var settings = SimulationSettings.FromTweaks();
        const ThermalMaterial material = ThermalMaterial.Base;
        const float fixedTemperature = 10f;
        var row = settings.ThermalMaterials[material] with { FixedTemperature = fixedTemperature, Loss = 0f };
        var engine = new TerrainHeatEngine(settings with { ThermalMaterials = settings.ThermalMaterials.With(material, row) });

        var pixels = new[] { TerrainPixel.Rock, TerrainPixel.BaseBarrier, TerrainPixel.Rock };
        var terrain = new[] { 500f, fixedTemperature, 500f };
        var air = new float[3];
        var ledger = new ThermalLedger();
        double start = engine.SumTotalEnergy(terrain, air, pixels);

        for (int step = 0; step < 50; step++)
            FullGridHeatStep.Step(engine, terrain, air, pixels, width: 3, height: 1, ledger);

        Assert.Equal(fixedTemperature, terrain[1]);
        double now = engine.SumTotalEnergy(terrain, air, pixels);
        Assert.Equal(start - ledger.SunkFixedTemperature - ledger.SunkAmbientLoss, now, tolerance: 0.05);
    }

    [Fact]
    public void EveryMachineType_HasABehavior()
    {
        // A failure here means MachineBehaviors.Get has no entry for a new MachineType.
        foreach (MachineType type in Enum.GetValues<MachineType>())
            Assert.NotNull(MachineBehaviors.Get(type));
    }
}
