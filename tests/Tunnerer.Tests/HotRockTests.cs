using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Core.LevelGen;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Thermal;
using Tunnerer.Core.Types;
using static Tunnerer.Tests.TankHeatTestHelpers;

namespace Tunnerer.Tests;

/// <summary>
/// Hot rock pockets: indestructible rock pinned at a high temperature, i.e. permanent heat sources that push heat into
/// any tunnel they touch.
/// </summary>
public class HotRockTests
{
    private const int Hot = (int)Tweaks.World.ThermalFixedHotRockTemperature;

    /// <summary>A dirt block with a two-row tunnel just below a pocket, so the pocket borders open air.</summary>
    private static TerrainGrid PocketNextToTunnel(out Position pocketCenter, out int tunnelY)
    {
        var t = new TerrainGrid(new Size(48, 40));
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 48; x++)
                t.SetPixelRaw(new Position(x, y), TerrainPixel.Dirt);

        tunnelY = 21;
        for (int x = 1; x < 47; x++)
        {
            t.SetPixelRaw(new Position(x, tunnelY), TerrainPixel.Blank);
            t.SetPixelRaw(new Position(x, tunnelY + 1), TerrainPixel.Blank);
        }

        pocketCenter = new Position(12, 17); // radius 3 reaches y=20, directly above the tunnel
        t.PlaceHotRockPocket(pocketCenter, radius: 3);
        return t;
    }

    [Fact]
    public void HotRock_IsSolidIndestructibleAndFixedTemperature()
    {
        Assert.True(Pixel.IsBlockingCollision(TerrainPixel.HotRock));
        Assert.False(Pixel.IsDiggable(TerrainPixel.HotRock));
        Assert.False(Pixel.IsTorchable(TerrainPixel.HotRock));
        Assert.False(Pixel.IsRock(TerrainPixel.HotRock), "ordinary rock can be blasted away; a pocket must not be");
        Assert.Equal(ThermalMaterial.HotRock, Pixel.GetThermalMaterial(TerrainPixel.HotRock));
        Assert.True(TerrainHeatEngine.IsFixedTemperatureMaterial(ThermalMaterial.HotRock));
        Assert.Equal(Tweaks.World.ThermalFixedHotRockTemperature, new TerrainHeatEngine().GetFixedTemperature(ThermalMaterial.HotRock));
    }

    [Fact]
    public void PlacePocket_ConvertsDirtAndRock_ButNotAirOrBases()
    {
        var t = new TerrainGrid(new Size(30, 30));
        for (int y = 0; y < 30; y++)
            for (int x = 0; x < 30; x++)
                t.SetPixelRaw(new Position(x, y), TerrainPixel.Dirt);
        t.SetPixelRaw(new Position(15, 15), TerrainPixel.Rock);
        t.SetPixelRaw(new Position(16, 15), TerrainPixel.Blank);
        t.SetPixelRaw(new Position(14, 15), TerrainPixel.BaseCore);
        t.SetPixelRaw(new Position(15, 16), TerrainPixel.Concrete);

        int converted = t.PlaceHotRockPocket(new Position(15, 15), radius: 3);

        Assert.True(converted > 20);
        Assert.Equal(TerrainPixel.HotRock, t.GetPixelRaw(new Position(15, 15))); // rock
        Assert.Equal(TerrainPixel.HotRock, t.GetPixelRaw(new Position(15, 14))); // dirt
        Assert.Equal(TerrainPixel.Blank, t.GetPixelRaw(new Position(16, 15)));
        Assert.Equal(TerrainPixel.BaseCore, t.GetPixelRaw(new Position(14, 15)));
        Assert.Equal(TerrainPixel.Concrete, t.GetPixelRaw(new Position(15, 16)));
        Assert.Equal(Hot, t.GetHeatTemperature(new Position(15, 14)), precision: 3);
    }

    [Fact]
    public void Pocket_StaysPinnedHot_AndHeatsTheTunnelItBorders()
    {
        var t = PocketNextToTunnel(out var pocket, out int tunnelY);

        for (int i = 0; i < 400; i++)
        {
            t.CoolDown();
            Assert.Equal(Hot, t.GetHeatTemperature(pocket), precision: 2); // pinned every step
        }

        float near = t.GetAirTemperature(new Position(pocket.X, tunnelY));
        float mid = t.GetAirTemperature(new Position(pocket.X + 8, tunnelY));
        float far = t.GetAirTemperature(new Position(pocket.X + 24, tunnelY));
        Assert.True(near > 20f, $"the tunnel next to the pocket should be hot. near={near:0.0}");
        Assert.True(near > mid && mid > far, $"heat should fall off along the tunnel. near={near:0.0}, mid={mid:0.0}, far={far:0.0}");
        Assert.True(far >= 0f);
    }

    [Fact]
    public void Pocket_IsASource_AndTheEnergyBalanceStillHolds()
    {
        var t = PocketNextToTunnel(out _, out _);

        double startEnergy = t.SumTotalThermalEnergy();
        double startNet = t.Ledger.NetExternal;
        double sunkAtStart = t.Ledger.SunkFixedTemperature;
        double maxResidual = 0.0;
        for (int i = 0; i < 400; i++)
        {
            t.CoolDown();
            double residual = (t.SumTotalThermalEnergy() - startEnergy) - (t.Ledger.NetExternal - startNet);
            maxResidual = Math.Max(maxResidual, Math.Abs(residual));
        }

        Assert.True(t.SumTotalThermalEnergy() > startEnergy + 1000.0, "the pocket keeps adding heat to the system");
        Assert.True(t.Ledger.SunkFixedTemperature < sunkAtStart,
            "the pin re-creates the heat that flowed out, which the ledger records as a negative sink");
        Assert.True(maxResidual < 0.5, $"the pocket's output must be fully accounted for. residual={maxResidual:0.0000}");
    }

    [Fact]
    public void Extraction_NeverDrawsFromAPocket()
    {
        var t = PocketNextToTunnel(out var pocket, out _);
        for (int i = 0; i < 50; i++)
            t.CoolDown();

        float terrainBefore = t.GetHeatTemperature(pocket);
        float airBefore = t.GetAirTemperature(pocket);
        float ringAirBefore = t.GetAirTemperature(new Position(pocket.X, pocket.Y + 4)); // tunnel air under the pocket

        // Draw everything possible from an area that contains the whole pocket and some tunnel.
        double taken = t.ExtractEnergyInRadiusArea(pocket, radius: 6, maxEnergy: 1e9, TerrainGrid.ThermalField.Terrain);
        taken += t.ExtractEnergyInRadiusArea(pocket, radius: 6, maxEnergy: 1e9, TerrainGrid.ThermalField.Air);

        Assert.Equal(terrainBefore, t.GetHeatTemperature(pocket));
        Assert.Equal(airBefore, t.GetAirTemperature(pocket));
        Assert.True(taken > 0.0, "the tunnel and the ground around the pocket still hold heat that can be taken");
        Assert.True(t.GetAirTemperature(new Position(pocket.X, pocket.Y + 4)) < ringAirBefore,
            "the heat is taken from the tunnel, not from the pocket");
    }

    [Fact]
    public void Relay_NextToAPocket_ClearsTunnelHeat_AndEnergyBalances()
    {
        var withRelay = RelayHeatTests.CreateScenario(seed: 4242);
        var control = RelayHeatTests.CreateScenario(seed: 4242);

        // A pocket in the rock beside the tunnel, level with the relay position.
        var pocketCenter = withRelay.RelayPos + new Offset(6, 0);
        Assert.True(withRelay.World.Terrain.PlaceHotRockPocket(pocketCenter, radius: 3) > 0);
        Assert.True(control.World.Terrain.PlaceHotRockPocket(pocketCenter, radius: 3) > 0);
        RelayHeatTests.PlantRelay(withRelay);

        double start = withRelay.World.SumSystemThermalEnergy();
        double startNet = withRelay.World.Terrain.Ledger.NetExternal;
        double maxResidual = 0.0;
        for (int i = 0; i < 300; i++)
        {
            withRelay.World.Advance(_ => default);
            control.World.Advance(_ => default);
            double residual = (withRelay.World.SumSystemThermalEnergy() - start) - (withRelay.World.Terrain.Ledger.NetExternal - startNet);
            maxResidual = Math.Max(maxResidual, Math.Abs(residual));
        }

        float Tunnel(RelayHeatTests.Scenario s) =>
            s.World.Terrain.SampleAverageHeat(s.RelayPos, radius: 2) * 255f;

        Assert.Equal(Hot, withRelay.World.Terrain.GetHeatTemperature(pocketCenter), precision: 2);
        Assert.True(Tunnel(control) > 10f, $"the pocket should heat the tunnel in the control world. tunnel={Tunnel(control):0.0}");
        Assert.True(Tunnel(withRelay) < Tunnel(control) * 0.7f,
            $"the relay should hold the tunnel cooler. withRelay={Tunnel(withRelay):0.0}, control={Tunnel(control):0.0}");
        Assert.True(maxResidual < 0.5, $"relay + pocket must balance. residual={maxResidual:0.0000}");
    }

    [Fact]
    public void Tank_BesideAPocket_HeatsUp_ButNotOutsideItsInfluence()
    {
        (World world, Tank tank, Position pocket) Setup(bool withPocket)
        {
            var (world, tank) = TestHelpers.CreateSeededWorldWithTank(seed: 6161);
            var center = GetWorldCenter(world);
            ClearArea(world.Terrain, center, radius: 6);
            var pocketCenter = center + new Offset(11, 0); // pocket edge (radius 4) at dx=7, next to the cavity wall
            if (withPocket)
                world.Terrain.PlaceHotRockPocket(pocketCenter, radius: 4);
            tank.Position = center + new Offset(5, 0);
            SetTankHeat(tank, 0f);
            return (world, tank, pocketCenter);
        }

        var (hotWorld, hotTank, _) = Setup(withPocket: true);
        var (coldWorld, coldTank, _) = Setup(withPocket: false);
        for (int i = 0; i < 300; i++)
        {
            hotWorld.Advance(_ => default);
            coldWorld.Advance(_ => default);
        }

        // Both tanks are off their network, so both gain the same off-network trickle (DESIGN.md §4.6).
        float offNetwork = 300 * Tweaks.Tank.OffNetworkHeatPerSecond / Tweaks.Perf.TargetFps;
        Assert.True(hotTank.Heat > coldTank.Heat + 5f, $"standing beside a pocket must heat the tank. heat={hotTank.Heat:0.0}, cold={coldTank.Heat:0.0}");
        Assert.True(hotTank.Heat < Tweaks.Tank.HeatSafeMax, $"the tank should not be cooked outright. heat={hotTank.Heat:0.0}");
        Assert.True(coldTank.Heat < offNetwork + 1f, $"without a pocket only the off-network trickle heats the tank. heat={coldTank.Heat:0.0}");
    }

    [Fact]
    public void WorldPockets_AreDeterministic_FarFromBasesAndEachOther()
    {
        var a = TestHelpers.CreateSeededWorld(seed: 8181, hotRockPocketCount: 4);
        var b = TestHelpers.CreateSeededWorld(seed: 8181, hotRockPocketCount: 4);
        var none = TestHelpers.CreateSeededWorld(seed: 8181);

        Assert.Equal(a.HotRockPockets, b.HotRockPockets);
        Assert.Empty(none.HotRockPockets);
        Assert.InRange(a.HotRockPockets.Count, 1, 4);

        int minBaseSq = Tweaks.World.HotRockMinDistanceFromBase * Tweaks.World.HotRockMinDistanceFromBase;
        int minBetweenSq = Tweaks.World.HotRockMinDistanceBetween * Tweaks.World.HotRockMinDistanceBetween;
        for (int i = 0; i < a.HotRockPockets.Count; i++)
        {
            var pocket = a.HotRockPockets[i];
            Assert.Equal(TerrainPixel.HotRock, a.Terrain.GetPixelRaw(pocket));
            foreach (var tankBase in a.TankBases.Bases)
                Assert.True(Position.DistanceSquared(pocket, tankBase.Position) >= minBaseSq, "pocket too close to a base");
            for (int j = i + 1; j < a.HotRockPockets.Count; j++)
                Assert.True(Position.DistanceSquared(pocket, a.HotRockPockets[j]) >= minBetweenSq, "pockets too close together");
        }
    }

    [Fact]
    public void WorldPockets_WithoutDecorationSeed_FollowTheLevelNotTheClock()
    {
        static IReadOnlyList<Position> Pockets(int seed)
        {
            var (terrain, spawns) = new ToastGenerator().Generate(TestHelpers.DefaultMapSize, seed: seed);
            var world = new World(TestHelpers.DefaultMapSize, simulationSeed: seed,
                hotRockPocketCount: 4);
            world.Initialize(terrain, spawns);
            return world.HotRockPockets;
        }

        var first = Pockets(8181);
        Assert.NotEmpty(first);
        Assert.Equal(first, Pockets(8181));
        Assert.NotEqual(first, Pockets(9292));
    }

    [Fact]
    public void WorldWithPockets_StartsWarmAroundThem_AndKeepsTheLedgerBalanced()
    {
        var world = TestHelpers.CreateSeededWorld(seed: 8181, enableTerrainRegrowth: false, hotRockPocketCount: 4);
        var pocket = world.HotRockPockets[0];

        // The pre-warm lets heat soak into the ground around the pocket before play starts.
        float ring = world.Terrain.GetHeatTemperature(new Position(pocket.X + Tweaks.World.HotRockMaxRadius + 2, pocket.Y));
        Assert.Equal(Hot, world.Terrain.GetHeatTemperature(pocket), precision: 2);
        Assert.True(ring > 1f || world.Terrain.GetHeatTemperature(new Position(pocket.X, pocket.Y + Tweaks.World.HotRockMaxRadius + 2)) > 1f,
            "ground just outside the pocket should already be warm");

        double start = world.SumSystemThermalEnergy();
        double startNet = world.Terrain.Ledger.NetExternal;
        double maxResidual = 0.0;
        for (int i = 0; i < 300; i++)
        {
            world.Advance(_ => default);
            double residual = (world.SumSystemThermalEnergy() - start) - (world.Terrain.Ledger.NetExternal - startNet);
            maxResidual = Math.Max(maxResidual, Math.Abs(residual));
        }

        Assert.True(maxResidual < 0.5, $"pockets must be fully accounted for in the ledger. residual={maxResidual:0.0000}");
    }
}

public class HotRockGradientTests
{
    [Trait("Category", "Slow")]
    [Fact]
    public void Pockets_FormAGradient_CoreAtTheCentre_AndPocketsClusterNearIt()
    {
        double sumFraction = 0;
        int pockets = 0, coresExpected = 0;
        for (int seed = 9000; seed < 9008; seed++)
        {
            var world = TestHelpers.CreateSeededWorld(seed: seed, hotRockPocketCount: Tweaks.World.HotRockPocketCount);
            var middle = new Position(world.Terrain.Width / 2, world.Terrain.Height / 2);

            // The core sits at the centre unless a base is too close to it (a base never gets a hot neighbour).
            int minSq = Tweaks.World.HotRockMinDistanceFromBase * Tweaks.World.HotRockMinDistanceFromBase;
            bool coreAllowed = world.TankBases.Bases.All(b => Position.DistanceSquared(middle, b.Position) >= minSq);
            if (coreAllowed)
            {
                coresExpected++;
                Assert.Contains(middle, world.HotRockPockets);
                Assert.Equal(TerrainPixel.HotRock, world.Terrain.GetPixelRaw(middle));
            }

            foreach (var pocket in world.HotRockPockets.Where(p => p != middle))
            {
                double fx = (pocket.X - middle.X) / (double)(world.Terrain.Width / 2);
                double fy = (pocket.Y - middle.Y) / (double)(world.Terrain.Height / 2);
                sumFraction += Math.Sqrt(fx * fx + fy * fy);
                pockets++;
            }
        }

        Assert.True(coresExpected > 0, "the seeds should include worlds where the core fits");
        Assert.True(pockets > 0);
        double mean = sumFraction / pockets;
        Assert.True(mean < 0.55, $"pockets should cluster toward the centre. meanDistanceFraction={mean:0.00}");
    }
}
