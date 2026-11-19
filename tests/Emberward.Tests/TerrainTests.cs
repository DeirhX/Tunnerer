using Emberward.Core.Terrain;
using Emberward.Core.Config;
using Emberward.Core.Thermal;
using Emberward.Core.Types;

namespace Emberward.Tests;

public class TerrainTests
{
    private static TerrainGrid MakeTerrain(int w, int h) => new(new Size(w, h));

    [Fact]
    public void SetPixelRaw_GetPixelRaw_Roundtrip()
    {
        var t = MakeTerrain(10, 10);
        var pos = new Position(5, 5);
        t.SetPixelRaw(pos, TerrainPixel.Dirt);
        Assert.Equal(TerrainPixel.Dirt, t.GetPixelRaw(pos));
    }

    [Fact]
    public void GetPixel_OutOfBounds_ReturnsRock()
    {
        var t = MakeTerrain(10, 10);
        Assert.Equal(TerrainPixel.Rock, t.GetPixel(new Position(-1, 0)));
        Assert.Equal(TerrainPixel.Rock, t.GetPixel(new Position(0, 10)));
        Assert.Equal(TerrainPixel.Rock, t.GetPixel(new Position(10, 5)));
    }

    [Fact]
    public void SetPixel_AddsToChangeList()
    {
        var t = MakeTerrain(10, 10);
        Assert.Empty(t.GetChangeList());

        t.SetPixel(new Position(3, 3), TerrainPixel.Dirt);
        Assert.Single(t.GetChangeList());
        Assert.Equal(new Position(3, 3), t.GetChangeList()[0]);
    }

    [Fact]
    public void DropSeenChanges_ForgetsWhatWasMarkedSeen_AndKeepsWhatCameAfter()
    {
        var t = MakeTerrain(10, 10);
        t.SetPixel(new Position(3, 3), TerrainPixel.Dirt);
        t.MarkChangesSeen();
        t.SetPixel(new Position(4, 4), TerrainPixel.Dirt);

        t.DropSeenChanges();
        Assert.Equal([new Position(4, 4)], t.GetChangeList());

        // Nothing new was marked, so nothing more is dropped.
        t.DropSeenChanges();
        Assert.Single(t.GetChangeList());
    }

    [Fact]
    public void CountLevelGenNeighbors_AllRock_Returns8()
    {
        var t = MakeTerrain(5, 5);
        for (int i = 0; i < 25; i++)
            t[i] = TerrainPixel.LevelGenRock;
        Assert.Equal(8, t.CountLevelGenNeighbors(new Position(2, 2)));
    }

    [Fact]
    public void CountLevelGenNeighbors_AllDirt_Returns0()
    {
        var t = MakeTerrain(5, 5);
        for (int i = 0; i < 25; i++)
            t[i] = TerrainPixel.LevelGenDirt;
        Assert.Equal(0, t.CountLevelGenNeighbors(new Position(2, 2)));
    }

    [Fact]
    public void CountLevelGenNeighbors_MixedPattern()
    {
        var t = MakeTerrain(5, 5);
        for (int i = 0; i < 25; i++)
            t[i] = TerrainPixel.LevelGenDirt;

        // Set 3 neighbors to rock
        t[1, 1] = TerrainPixel.LevelGenRock; // top-left of (2,2)
        t[2, 1] = TerrainPixel.LevelGenRock; // top of (2,2)
        t[3, 1] = TerrainPixel.LevelGenRock; // top-right of (2,2)

        Assert.Equal(3, t.CountLevelGenNeighbors(new Position(2, 2)));
    }

    [Fact]
    public void CountDirtNeighbors_Matches()
    {
        var t = MakeTerrain(5, 5);
        for (int i = 0; i < 25; i++)
            t[i] = TerrainPixel.Blank;

        t[1, 1] = TerrainPixel.Dirt;
        t[2, 1] = TerrainPixel.Dirt;
        t[3, 1] = TerrainPixel.DirtGrow; // not counted as dirt
        t[1, 2] = TerrainPixel.Dirt;

        Assert.Equal(3, t.CountDirtNeighbors(new Position(2, 2)));
    }

    [Fact]
    public void HasLevelGenNeighbor_DetectsAdjacentDirt()
    {
        var t = MakeTerrain(5, 5);
        for (int i = 0; i < 25; i++)
            t[i] = TerrainPixel.LevelGenRock;

        Assert.False(t.HasLevelGenNeighbor(2, 2));

        t[1, 1] = TerrainPixel.LevelGenDirt;
        Assert.True(t.HasLevelGenNeighbor(2, 2));
    }

    [Fact]
    public void MaterializeTerrain_ConvertsLevelGenToGamePixels()
    {
        var t = MakeTerrain(10, 10);
        for (int i = 0; i < 100; i++)
            t[i] = i % 2 == 0 ? TerrainPixel.LevelGenDirt : TerrainPixel.LevelGenRock;

        t.MaterializeTerrain();

        for (int i = 0; i < 100; i++)
        {
            if (i % 2 == 0)
                Assert.True(Pixel.IsDirt(t[i]), $"Expected dirt at offset {i}, got {t[i]}");
            else
                Assert.Equal(TerrainPixel.Rock, t[i]);
        }
    }

    [Fact]
    public void DrawAllToSurface_FillsSurface()
    {
        var t = MakeTerrain(4, 4);
        for (int i = 0; i < 16; i++)
            t[i] = TerrainPixel.Dirt;

        var surface = new uint[16];
        t.DrawAllToSurface(surface);

        uint expected = Pixel.GetColor(TerrainPixel.Dirt).ToArgb();
        Assert.All(surface, px => Assert.Equal(expected, px));
    }

    [Fact]
    public void DrawChangesToSurface_OnlyUpdatesChanged()
    {
        var t = MakeTerrain(4, 4);
        for (int i = 0; i < 16; i++)
            t[i] = TerrainPixel.Blank;

        var surface = new uint[16];
        t.DrawAllToSurface(surface);

        uint blankColor = Pixel.GetColor(TerrainPixel.Blank).ToArgb();
        uint dirtColor = Pixel.GetColor(TerrainPixel.Dirt).ToArgb();

        t.SetPixel(new Position(1, 1), TerrainPixel.Dirt);
        t.DrawChangesToSurface(surface);

        Assert.Equal(dirtColor, surface[1 + 1 * 4]);
        Assert.Equal(blankColor, surface[0]); // unchanged
    }

    [Fact]
    public void IndexerByOffset_MatchesPosition()
    {
        var t = MakeTerrain(10, 8);
        t.SetPixelRaw(new Position(3, 5), TerrainPixel.Rock);
        Assert.Equal(TerrainPixel.Rock, t[3 + 5 * 10]);
    }

    [Fact]
    public void MaterialHeatExchange_AndAmbient_DriveTowardEquilibrium()
    {
        var t = MakeTerrain(16, 16);
        for (int i = 0; i < 16 * 16; i++)
            t[i] = TerrainPixel.Rock;

        var hot = new Position(8, 8);
        t.AddHeat(hot, 255);

        float startCenter = t.GetHeatTemperature(hot);
        for (int i = 0; i < 200; i++)
            t.CoolDown();

        float endCenter = t.GetHeatTemperature(hot);
        float ambient = Tweaks.World.ThermalAmbientTemperature;

        Assert.True(endCenter < startCenter, $"Expected hot spot to cool from {startCenter}, got {endCenter}");
        Assert.InRange(endCenter, ambient - 10f, ambient + 30f);
    }

    [Fact]
    public void CoolDown_NoSources_FirstTick_DoesNotIncreaseTotalHeat()
    {
        var t = MakeTerrain(20, 20);
        for (int i = 0; i < 20 * 20; i++)
            t[i] = TerrainPixel.Rock;

        var hot = new Position(10, 10);
        t.AddHeatRadius(hot, 255, 6);

        double startTotal = SumHeat(t);
        t.CoolDown();
        double afterFirstTick = SumHeat(t);
        Assert.True(afterFirstTick <= startTotal + 0.001,
            $"Expected first CoolDown tick to be non-increasing. start={startTotal}, tick1={afterFirstTick}");
    }

    [Fact]
    public void MaterialHeatExchange_NoSources_TotalHeatNeverIncreasesPerTick()
    {
        var t = MakeTerrain(20, 20);
        for (int i = 0; i < 20 * 20; i++)
            t[i] = TerrainPixel.Rock;

        var hot = new Position(10, 10);
        t.AddHeatRadius(hot, 255, 6);
        double previousTotal = SumHeat(t);
        double startTotal = previousTotal;
        double maxUpstep = 0;

        for (int i = 0; i < 300; i++)
        {
            t.CoolDown();
            double currentTotal = SumHeat(t);
            double upstep = currentTotal - previousTotal;
            if (upstep > maxUpstep)
                maxUpstep = upstep;

            Assert.True(currentTotal <= previousTotal + 0.001,
                $"Expected non-increasing total heat at tick {i}. prev={previousTotal}, current={currentTotal}");
            previousTotal = currentTotal;
        }

        double endTotal = previousTotal;
        Assert.True(endTotal <= startTotal + 0.001, $"Expected non-increasing total heat. start={startTotal}, end={endTotal}");
        Assert.True(maxUpstep <= 0.001, $"Expected no positive up-step, got {maxUpstep:0.000000}");
    }

    [Fact]
    public void TerrainHeatEngine_OneDimensionalStrip_ClosedSystem_ConvergesToInitialEnergyMean()
    {
        // Single-pixel-wide bed (1 x N): inject heat on one side and wait for equilibrium.
        AssertClosedRockSystemConverges(width: 1, height: 64, hotIndex: 0, hotTemperature: 220f, steps: 30000, tolerance: 3f);
    }

    [Trait("Category", "Slow")]
    [Fact]
    public void TerrainHeatEngine_SquareClosedSystem_ConvergesToInitialEnergyMean()
    {
        // Hot spot in the centre of a square.
        const int size = 32;
        AssertClosedRockSystemConverges(size, size, hotIndex: size / 2 + (size / 2) * size, hotTemperature: 255f, steps: 60000, tolerance: 2f);
    }

    /// <summary>
    /// A closed block of rock must keep its total energy and settle, in both the terrain and the air field, at the
    /// temperature that energy implies.
    /// </summary>
    private static void AssertClosedRockSystemConverges(int width, int height, int hotIndex, float hotTemperature, int steps, float tolerance)
    {
        var engine = new TerrainHeatEngine(FullGridHeatStep.LosslessSettings());
        int len = width * height;
        var terrain = new float[len];
        var air = new float[len];
        var pixels = new TerrainPixel[len];
        Array.Fill(pixels, TerrainPixel.Rock);
        terrain[hotIndex] = hotTemperature;

        double startEnergy = engine.SumTotalEnergy(terrain, air, pixels);
        double capacityPerCell = engine.GetHeatCapacity(ThermalMaterial.Stone) + engine.AirHeatCapacity;
        float expected = (float)(startEnergy / (len * capacityPerCell));

        double maxAbsDrift = 0.0;
        for (int step = 0; step < steps; step++)
        {
            FullGridHeatStep.Step(engine, terrain, air, pixels, width, height);
            maxAbsDrift = Math.Max(maxAbsDrift, Math.Abs(engine.SumTotalEnergy(terrain, air, pixels) - startEnergy));
        }

        Assert.True(maxAbsDrift <= 0.5, $"Total energy drifted by {maxAbsDrift:0.0000} from {startEnergy:0.0000}");
        Assert.All(terrain, t => Assert.InRange(t, expected - tolerance, expected + tolerance));
        Assert.All(air, a => Assert.InRange(a, expected - tolerance, expected + tolerance));
    }

    [Fact]
    public void TerrainHeatEngine_ConservativeAirField_ClosedSystem_PreservesTotalEnergy()
    {
        const int width = 32;
        const int height = 32;
        var engine = new TerrainHeatEngine(FullGridHeatStep.LosslessSettings());
        var terrain = new float[width * height];
        var air = new float[width * height];
        var pixels = new TerrainPixel[width * height];

        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = TerrainPixel.Rock;

        int cx = width / 2;
        int cy = height / 2;
        terrain[cx + cy * width] = 255f;

        double startEnergy = engine.SumTotalEnergy(terrain, air, pixels);
        double maxAbsDrift = 0.0;
        for (int step = 0; step < 20000; step++)
        {
            FullGridHeatStep.Step(engine, terrain, air, pixels, width, height);
            double now = engine.SumTotalEnergy(terrain, air, pixels);
            double drift = Math.Abs(now - startEnergy);
            if (drift > maxAbsDrift)
                maxAbsDrift = drift;
        }

        Assert.True(maxAbsDrift <= 0.5,
            $"Expected conservative closed-system behavior. start={startEnergy:0.000}, maxAbsDrift={maxAbsDrift:0.000}");
    }

    private static double SumHeat(TerrainGrid terrain)
    {
        double sum = 0.0;
        for (int i = 0; i < terrain.Size.Area; i++)
            sum += terrain.GetHeatTemperature(i);
        return sum;
    }

}
