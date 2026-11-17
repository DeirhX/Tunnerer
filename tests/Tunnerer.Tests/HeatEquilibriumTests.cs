using Tunnerer.Core.Config;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Thermal;
using Tunnerer.Core.Types;

namespace Tunnerer.Tests;

/// <summary>
/// The rules that let heat settle: dirt insulates, air carries heat only through open space, ground and open air
/// lose a little to the outside every tick, and the solver leaves a tile alone once nothing in it changes.
/// </summary>
public class HeatEquilibriumTests
{
    private const int TicksPerSecond = Tweaks.Perf.TargetFps;

    [Fact]
    public void Air_DoesNotCarryHeat_FromOneSolidCellToTheNext()
    {
        // Two dirt cells at the same temperature, warm air inside the left one only. The warm air can only settle
        // into its own cell: the air inside the right cell hears nothing of it.
        var engine = new TerrainHeatEngine();
        var pixels = new[] { TerrainPixel.Dirt, TerrainPixel.Dirt };
        var terrain = new float[2];
        var air = new[] { 50f, 0f };

        FullGridHeatStep.Step(engine, terrain, air, pixels, width: 2, height: 1);

        Assert.True(air[0] < 50f && terrain[0] > 0f, "the warm air settles into its own cell");
        Assert.Equal(0f, air[1]);
        Assert.Equal(0f, terrain[1]);
    }

    [Fact]
    public void Air_CarriesHeat_WhereACellIsOpen()
    {
        var engine = new TerrainHeatEngine();
        var pixels = new[] { TerrainPixel.Dirt, TerrainPixel.Blank };
        var terrain = new float[2];
        var air = new[] { 50f, 0f };

        FullGridHeatStep.Step(engine, terrain, air, pixels, width: 2, height: 1);

        Assert.True(air[1] > 0f, "air inside the ground reaches the open cell beside it");
    }

    [Fact]
    public void Dirt_ConductsFarWorseThanStone()
    {
        var table = Tweaks.World.ThermalMaterials;
        float dirt = table.PairConductance(ThermalMaterial.Dirt, ThermalMaterial.Dirt);
        float stone = table.PairConductance(ThermalMaterial.Stone, ThermalMaterial.Stone);
        Assert.True(stone >= 20f * dirt, $"dirt is the insulator. dirt={dirt}, stone={stone}");
    }

    [Theory]
    [InlineData(TerrainPixel.Dirt, 40f)]
    [InlineData(TerrainPixel.Dirt, -40f)]
    [InlineData(TerrainPixel.Rock, 40f)]
    [InlineData(TerrainPixel.Blank, 40f)]
    [InlineData(TerrainPixel.Blank, -40f)]
    public void EvenlyWarmOrColdGround_ComesBackTowardAmbient_AndTheLedgerBooksIt(TerrainPixel pixel, float temperature)
    {
        // The whole map at one temperature: nothing can conduct anywhere, so only the loss to the outside acts.
        var t = new TerrainGrid(new Size(40, 40));
        t.Fill(pixel);
        var center = new Position(20, 20);
        double energy = temperature * (t.SumHeatCapacityInRadiusArea(center, 40, TerrainGrid.ThermalField.Terrain)
            + (pixel == TerrainPixel.Blank ? 0.0 : t.SumHeatCapacityInRadiusArea(center, 40, TerrainGrid.ThermalField.Air)));
        t.AddEnergyInRadiusArea(center, 40, temperature * t.SumHeatCapacityInRadiusArea(center, 40, TerrainGrid.ThermalField.Terrain),
            TerrainGrid.ThermalField.Terrain);
        if (pixel != TerrainPixel.Blank)
            t.AddEnergyInRadiusArea(center, 40, temperature * t.SumHeatCapacityInRadiusArea(center, 40, TerrainGrid.ThermalField.Air),
                TerrainGrid.ThermalField.Air);
        Assert.Equal(energy, t.SumTotalThermalEnergy(), tolerance: Math.Abs(energy) * 1e-5);

        for (int i = 0; i < 20 * TicksPerSecond; i++)
            t.CoolDown();

        float now = t.GetHeatTemperature(center);
        Assert.True(Math.Abs(now) < Math.Abs(temperature) && Math.Sign(now) == Math.Sign(temperature),
            $"expected {temperature} to come back toward ambient without crossing it, got {now}");
        // Every cell did the same: the field is still even.
        Assert.Equal(now, t.GetHeatTemperature(new Position(3, 37)), precision: 3);
        // What left is in the ledger, with the sign of what left: heat for warm ground, cold for cold ground.
        Assert.Equal(energy - t.Ledger.SunkAmbientLoss, t.SumTotalThermalEnergy(), tolerance: Math.Abs(energy) * 1e-4);
        Assert.Equal(Math.Sign(temperature), Math.Sign(t.Ledger.SunkAmbientLoss));
    }

    [Fact]
    public void OpenAir_LosesHeatFasterThanGround()
    {
        float After(TerrainPixel pixel)
        {
            var t = new TerrainGrid(new Size(40, 40));
            t.Fill(pixel);
            var center = new Position(20, 20);
            t.AddEnergyInRadiusArea(center, 40, 40.0 * t.SumHeatCapacityInRadiusArea(center, 40, TerrainGrid.ThermalField.Terrain),
                TerrainGrid.ThermalField.Terrain);
            if (pixel != TerrainPixel.Blank)
                t.AddEnergyInRadiusArea(center, 40, 40.0 * t.SumHeatCapacityInRadiusArea(center, 40, TerrainGrid.ThermalField.Air),
                    TerrainGrid.ThermalField.Air);
            for (int i = 0; i < 10 * TicksPerSecond; i++)
                t.CoolDown();
            return t.GetHeatTemperature(center);
        }

        // A dug-out chamber is the radiator; the ground around it holds on to its heat.
        Assert.True(After(TerrainPixel.Blank) < After(TerrainPixel.Dirt));
    }

    [Fact]
    public void UntouchedGround_IsNeverStepped_AndDiggingColdGroundWakesNothing()
    {
        var t = new TerrainGrid(new Size(160, 160));
        t.Fill(TerrainPixel.Dirt);

        t.CoolDown();
        Assert.Equal(0, t.LastCoolDownProfile.SteppedTiles);

        for (int x = 20; x < 120; x++)
            t.SetPixel(new Position(x, 80), TerrainPixel.Blank);
        t.CoolDown();
        Assert.Equal(0, t.LastCoolDownProfile.SteppedTiles);
    }

    [Fact]
    public void HeatAddedSomewhere_StepsOnlyTheTilesAroundIt()
    {
        var t = new TerrainGrid(new Size(640, 640));
        t.Fill(TerrainPixel.Dirt);
        t.GetThermalTileInfo(out int tileSize, out int tilesX, out int tilesY);

        t.AddHeat(new Position(tileSize * 10 + tileSize / 2, tileSize * 10 + tileSize / 2), 80);
        t.CoolDown();

        // The tile that was written to and the ring around it, out of 400.
        Assert.Equal(9, t.LastCoolDownProfile.SteppedTiles);
        Assert.Equal(400, tilesX * tilesY);
    }

    [Trait("Category", "Slow")]
    [Fact]
    public void HeatLeftInTheGround_FadesOut_AndItsTilesGoBackToSleep()
    {
        var t = new TerrainGrid(new Size(96, 96));
        t.Fill(TerrainPixel.Dirt);
        for (int x = 30; x < 66; x++)
            t.SetPixelRaw(new Position(x, 48), TerrainPixel.Blank);
        t.AddHeatRadius(new Position(48, 48), amount: 40, radius: 5);

        int ticks = RunUntilAsleep(t, maxSeconds: 1200);

        Assert.True(ticks < 1200 * TicksPerSecond, "the heat never finished fading");
        float warmest = 0f;
        for (int i = 0; i < t.Size.Area; i++)
            warmest = Math.Max(warmest, Math.Max(Math.Abs(t.GetHeatTemperature(i)), Math.Abs(t.GetAirTemperature(i))));
        Assert.True(warmest < 0.5f, $"what stays behind when the tiles sleep must be next to nothing, got {warmest:0.000}");
    }

    [Fact]
    public void DiggingGroundThatOnlyHoldsATraceOfOldHeat_WakesNothing()
    {
        var t = new TerrainGrid(new Size(160, 160));
        t.Fill(TerrainPixel.Dirt);
        var spot = new Position(80, 80);
        // Less than the wake temperature: what faded heat leaves behind when its tile goes to sleep.
        t.AddEnergyInRadiusArea(spot, 3, 0.3 * t.SumHeatCapacityInRadiusArea(spot, 3, TerrainGrid.ThermalField.Terrain),
            TerrainGrid.ThermalField.Terrain);
        RunUntilAsleep(t, maxSeconds: 60);

        t.SetPixel(spot, TerrainPixel.Blank);
        t.CoolDown();
        Assert.Equal(0, t.LastCoolDownProfile.SteppedTiles);

        // Properly warm ground is another matter.
        t.AddEnergyInRadiusArea(spot, 3, 20.0 * t.SumHeatCapacityInRadiusArea(spot, 3, TerrainGrid.ThermalField.Terrain),
            TerrainGrid.ThermalField.Terrain);
        RunUntilAsleep(t, maxSeconds: 1);
        t.SetPixel(new Position(81, 80), TerrainPixel.Blank);
        t.CoolDown();
        Assert.True(t.LastCoolDownProfile.SteppedTiles > 0);
    }

    [Fact]
    public void SettleConstants_FitTogether()
    {
        var materials = Tweaks.World.ThermalMaterials;
        float smallestLoss = Enum.GetValues<ThermalMaterial>()
            .Select(m => materials[m].Loss).Where(loss => loss > 0f).Min();
        // Fading heat is followed down to FadeStep / loss degrees; a wake temperature below that would let every
        // dig or regrowth in once-warm ground wake its tile.
        Assert.True(Tweaks.World.ThermalWakeTemperature > Tweaks.World.ThermalSettledFadeStep / smallestLoss);
        // A cut-off near the settle steps makes flows switch on and off and keeps settled halos awake.
        Assert.True(Tweaks.World.ThermalFlowCutoff * 100f <= Tweaks.World.ThermalSettledFadeStep);
        Assert.True(Tweaks.World.ThermalSettledFadeStep <= Tweaks.World.ThermalSettledGrowStep);
    }

    [Fact]
    public void TilesSteppedSeparately_TradeAcrossTheirSides_LikeOneRegion()
    {
        // A field with every kind of neighbour pair, stepped once as one region and once as four tiles that trade
        // across their shared sides. Each cell must get the same change either way.
        const int size = 16, half = size / 2;
        var engine = new TerrainHeatEngine();
        var pixels = new TerrainPixel[size * size];
        var terrain = new float[size * size];
        var air = new float[size * size];
        var materials = new[] { TerrainPixel.Dirt, TerrainPixel.Rock, TerrainPixel.Blank, TerrainPixel.Dirt, TerrainPixel.HotRock };
        var rng = new Random(5);
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = materials[rng.Next(materials.Length)];
            air[i] = rng.Next(-40, 120);
            terrain[i] = pixels[i] == TerrainPixel.Blank ? air[i] : rng.Next(-40, 120);
        }

        var wholeTerrain = new float[size * size];
        var wholeAir = new float[size * size];
        engine.ComputeRegionDeltaConservative(terrain, air, pixels, size, size, 0, 0, size - 1, size - 1, wholeTerrain, wholeAir);

        var tileTerrain = new float[half * half];
        var tileAir = new float[half * half];
        for (int ty = 0; ty < 2; ty++)
        {
            for (int tx = 0; tx < 2; tx++)
            {
                RegionEdges shared = (tx == 0 ? RegionEdges.Right : RegionEdges.Left) | (ty == 0 ? RegionEdges.Down : RegionEdges.Up);
                engine.ComputeRegionDeltaConservative(terrain, air, pixels, size, size,
                    tx * half, ty * half, tx * half + half - 1, ty * half + half - 1, tileTerrain, tileAir, shared);
                for (int y = 0; y < half; y++)
                {
                    for (int x = 0; x < half; x++)
                    {
                        int whole = tx * half + x + (ty * half + y) * size;
                        Assert.Equal(wholeTerrain[whole], tileTerrain[x + y * half], tolerance: 1e-4f);
                        Assert.Equal(wholeAir[whole], tileAir[x + y * half], tolerance: 1e-4f);
                    }
                }
            }
        }
    }

    [Fact]
    public void SteppingOnManyThreads_GivesExactlyTheResultOfOne()
    {
        // Lockstep play needs the same bits on every machine, whatever its core count.
        TerrainGrid Run(int parallelFromTiles)
        {
            var t = new TerrainGrid(new Size(256, 192),
                SimulationSettings.FromTweaks() with { ThermalParallelTileThreshold = parallelFromTiles });
            t.Fill(TerrainPixel.Dirt);
            for (int x = 20; x < 230; x++)
                t.SetPixelRaw(new Position(x, 96), TerrainPixel.Blank);
            for (int y = 30; y < 160; y++)
                t.SetPixelRaw(new Position(128, y), TerrainPixel.Rock);
            t.PlaceHotRockPocket(new Position(60, 90), 4);
            t.PlaceFrostRockPocket(new Position(190, 100), 3);
            t.AddHeatRadius(new Position(128, 96), amount: 200, radius: 9);
            for (int i = 0; i < 400; i++)
                t.CoolDown();
            return t;
        }

        TerrainGrid one = Run(int.MaxValue), many = Run(1);
        for (int i = 0; i < one.Size.Area; i++)
        {
            Assert.Equal(one.GetHeatTemperature(i), many.GetHeatTemperature(i));
            Assert.Equal(one.GetAirTemperature(i), many.GetAirTemperature(i));
        }
        Assert.Equal(one.Ledger.SunkAmbientLoss, many.Ledger.SunkAmbientLoss);
        Assert.Equal(one.Ledger.SunkFixedTemperature, many.Ledger.SunkFixedTemperature);
    }

    [Fact]
    public void ARegionsSides_AreClosed_UnlessNamed()
    {
        // Hot cell on the left of a tile's side, cold on the right. With the side closed nothing crosses.
        var engine = new TerrainHeatEngine();
        var pixels = new[] { TerrainPixel.Rock, TerrainPixel.Rock };
        var terrain = new[] { 100f, 0f };
        var air = new float[2];
        var terrainDelta = new float[1];
        var airDelta = new float[1];

        engine.ComputeRegionDeltaConservative(terrain, air, pixels, 2, 1, 1, 0, 1, 0, terrainDelta, airDelta);
        Assert.Equal(0f, terrainDelta[0]);

        engine.ComputeRegionDeltaConservative(terrain, air, pixels, 2, 1, 1, 0, 1, 0, terrainDelta, airDelta, RegionEdges.Left);
        Assert.True(terrainDelta[0] > 0f, "the cold cell should warm from its neighbour across the open side");
    }

    /// <summary>Steps the grid until the solver has nothing left to step; returns the ticks it took.</summary>
    private static int RunUntilAsleep(TerrainGrid t, int maxSeconds)
    {
        int ticks = 0;
        do
        {
            t.CoolDown();
            ticks++;
        }
        while (t.LastCoolDownProfile.SteppedTiles > 0 && ticks < maxSeconds * TicksPerSecond);
        return ticks;
    }

    /// <summary>
    /// Runs a buried pocket to its equilibrium, which takes long: in a class of its own so that xUnit runs it beside
    /// the others.
    /// </summary>
    public class PocketHaloTests
    {
        [Trait("Category", "Slow")]
        [Fact]
        public void BuriedPocket_KeepsAHaloOfAFixedSize_AndThenCostsNothing_UntilItIsDisturbed()
        {
            var t = new TerrainGrid(new Size(224, 224));
            t.Fill(TerrainPixel.Dirt);
            var pocket = new Position(112, 112);
            const int radius = 4;
            t.PlaceHotRockPocket(pocket, radius);

            int ticks = RunUntilAsleep(t, maxSeconds: 1200);
            Assert.True(ticks < 1200 * TicksPerSecond, "a buried pocket must reach an equilibrium with the ground around it");

            // The halo: warm enough for the radar (10 degrees) several cells out, gone well before the map's edge.
            float At(int cellsFromPocketEdge) => t.GetHeatTemperature(new Position(pocket.X + radius + cellsFromPocketEdge, pocket.Y));
            Assert.True(At(6) >= Tweaks.Radar.HeatThreshold, $"the radar should see the ground around a pocket. 6 cells out: {At(6):0.0}");
            Assert.True(At(30) < Tweaks.Radar.HeatThreshold / 2f, $"the halo must stay near the pocket. 30 cells out: {At(30):0.0}");
            Assert.True(At(60) < 1f, $"far ground must stay cold. 60 cells out: {At(60):0.00}");

            // Asleep means asleep: more ticks change nothing and step nothing.
            float haloBefore = At(6);
            for (int i = 0; i < 100; i++)
                t.CoolDown();
            Assert.Equal(0, t.LastCoolDownProfile.SteppedTiles);
            Assert.Equal(haloBefore, At(6));

            // Uncork it: a tunnel dug up to the pocket starts the heat moving again, into the open space.
            for (int x = pocket.X + radius + 1; x <= pocket.X + radius + 40; x++)
                t.SetPixel(new Position(x, pocket.Y), TerrainPixel.Blank);
            t.CoolDown();
            Assert.True(t.LastCoolDownProfile.SteppedTiles > 0, "digging into settled warm ground must wake it");
            float tunnelBefore = t.GetAirTemperature(new Position(pocket.X + radius + 20, pocket.Y));
            for (int i = 0; i < 60 * TicksPerSecond; i++)
                t.CoolDown();
            Assert.True(t.GetAirTemperature(new Position(pocket.X + radius + 20, pocket.Y)) > tunnelBefore + 1f,
                "the pocket should leak into the tunnel that reached it");
        }
    }

    /// <summary>
    /// Runs a buried pocket to its equilibrium, which takes long: in a class of its own so that xUnit runs it beside
    /// the others.
    /// </summary>
    public class HaloSupplyTests
    {
        [Trait("Category", "Slow")]
        [Fact]
        public void ActivityBesideASettledHalo_DoesNotCutTheHaloOffFromItsPocket()
        {
            // The pocket sits in the middle of its tile; its halo reaches into the tiles around it, fed across the
            // sides between them. Heat added a tile further on steps the halo tile but, by the ring alone, not the
            // pocket's tile. The side between them must stay open, or the halo loses its supply for as long as that lasts.
            var t = new TerrainGrid(new Size(288, 224));
            t.Fill(TerrainPixel.Dirt);
            t.PlaceHotRockPocket(new Position(112, 112), 4);
            RunUntilAsleep(t, maxSeconds: 1200);
            var haloCell = new Position(128, 112); // first cell of the tile right of the pocket's tile
            float before = t.GetHeatTemperature(haloCell);
            Assert.True(before > 3f, $"the halo should reach into the next tile. got {before:0.00}");

            t.AddHeat(new Position(176, 112), 30);
            t.CoolDown();

            // A settled halo creeps by less than the settle step a tick; a cut supply cost it 0.024 degrees at once.
            Assert.Equal(before, t.GetHeatTemperature(haloCell), tolerance: 2f * Tweaks.World.ThermalSettledGrowStep);
            // And the pocket's tiles stay settled: only the tile that was heated is active.
            Assert.True(t.LastCoolDownProfile.SteppedTiles > 9, "the pocket's tiles are stepped along, across the open sides");
            t.CoolDown();
            Assert.True(t.LastCoolDownProfile.ActiveTiles <= 2,
                $"stepping a settled pocket along must not wake it. active={t.LastCoolDownProfile.ActiveTiles}");
        }
    }
}
