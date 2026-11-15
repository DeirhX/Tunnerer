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
        // TerrainHeatEngine indexes its capacity and conductance tables by (int)ThermalMaterial.
        int[] values = Enum.GetValues<ThermalMaterial>().Select(m => (int)m).Order().ToArray();
        Assert.Equal(Enumerable.Range(0, values.Length), values);
    }

    [Fact]
    public void EveryThermalMaterial_HasAHeatCapacity()
    {
        // A failure here means TerrainHeatEngine.MaterialCount, ComputeHeatCapacity or ComputeConductance
        // was not updated for a new ThermalMaterial.
        var engine = new TerrainHeatEngine();
        foreach (ThermalMaterial material in Enum.GetValues<ThermalMaterial>())
            Assert.True(engine.GetHeatCapacity(material) > 0f, $"{material} has no positive heat capacity");
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
        Assert.True(TerrainHeatEngine.IsFixedTemperatureMaterial(material));
        float fixedTemperature = engine.GetFixedTemperature(material);

        var pixels = new[] { TerrainPixel.Rock, pinned, TerrainPixel.Rock };
        var terrain = new[] { 500f, fixedTemperature, 500f };
        var air = new float[3];
        var ledger = new ThermalLedger();
        double start = engine.SumTotalEnergy(terrain, air, pixels);

        for (int step = 0; step < 50; step++)
            engine.StepConservative(terrain, air, pixels, width: 3, height: 1, ledger);

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
