namespace Emberward.Tests;

using Emberward.Core;
using Emberward.Core.LevelGen;
using Emberward.Core.Match;
using Emberward.Core.Terrain;
using Emberward.Core.Types;
using Emberward.Core.Entities.Tanks;

internal static class TestHelpers
{
    public const int DefaultSeed = 42;
    public static readonly Size DefaultMapSize = new(320, 200);

    public static World CreateSeededWorld(
        Size? size = null,
        int seed = DefaultSeed,
        bool enableTerrainRegrowth = true,
        int hotRockPocketCount = 0,
        int frostRockPocketCount = 0,
        bool seedOre = false,
        bool energyNetwork = false,
        bool machineUpgrades = false,
        bool tankReserve = false,
        bool mobs = false)
    {
        var mapSize = size ?? DefaultMapSize;
        var gen = new ToastGenerator();
        var (terrain, spawns) = gen.Generate(mapSize, seed: seed);
        var world = new World(
            mapSize,
            simulationSeed: seed,
            rules: new MatchRules
            {
                TerrainRegrowth = enableTerrainRegrowth,
                HotRockPockets = hotRockPocketCount,
                FrostRockPockets = frostRockPocketCount,
                SeedOre = seedOre,
                EnergyNetwork = energyNetwork,
                MachineUpgrades = machineUpgrades,
                TankReserve = tankReserve,
                Mobs = mobs,
            });
        world.Initialize(terrain, spawns, decorationSeed: seed + 1);
        return world;
    }

    /// <summary><see cref="CreateSeededWorld"/> plus the world's first tank.</summary>
    public static (World world, Tank tank) CreateSeededWorldWithTank(int seed = DefaultSeed, bool enableTerrainRegrowth = true)
    {
        var world = CreateSeededWorld(seed: seed, enableTerrainRegrowth: enableTerrainRegrowth);
        return (world, world.TankList.Tanks[0]);
    }

    public static double CountDirtPercentage(TerrainGrid terrain, Size size)
    {
        int dirtCount = 0;
        for (int i = 0; i < size.Area; i++)
            if (Pixel.IsDirt(terrain[i])) dirtCount++;
        return 100.0 * dirtCount / size.Area;
    }

    public static void AssertBorderIsRock(TerrainGrid terrain, int w, int h)
    {
        for (int x = 0; x < w; x++)
        {
            Assert.Equal(TerrainPixel.LevelGenRock, terrain.GetPixelRaw(new Position(x, 0)));
            Assert.Equal(TerrainPixel.LevelGenRock, terrain.GetPixelRaw(new Position(x, h - 1)));
        }
        for (int y = 0; y < h; y++)
        {
            Assert.Equal(TerrainPixel.LevelGenRock, terrain.GetPixelRaw(new Position(0, y)));
            Assert.Equal(TerrainPixel.LevelGenRock, terrain.GetPixelRaw(new Position(w - 1, y)));
        }
    }

    public static void AssertNoLevelGenPixels(TerrainGrid terrain, Size size)
    {
        for (int i = 0; i < size.Area; i++)
        {
            var pix = terrain[i];
            Assert.False(pix == TerrainPixel.LevelGenDirt || pix == TerrainPixel.LevelGenRock || pix == TerrainPixel.LevelGenMark,
                $"LevelGen pixel {pix} survived at offset {i}");
        }
    }
}
