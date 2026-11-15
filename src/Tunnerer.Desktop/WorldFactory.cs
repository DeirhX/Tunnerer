namespace Tunnerer.Desktop;

using System.Diagnostics;
using System.Globalization;
using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.LevelGen;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Config;

/// <summary>What the command line asked for, as far as building the world goes.</summary>
/// <param name="TerrainTest">Static test patterns (<c>--terrain-test</c>) instead of a generated match.</param>
/// <param name="TerrainSizeOverride"><c>--size</c> / <c>--large</c>; null uses the default for the mode.</param>
/// <param name="GenMode"><see cref="LevelGenMode.Optimized"/> for <c>--fast</c>.</param>
/// <param name="Seed">Map and simulation seed (see <c>Game.Seed</c>).</param>
public readonly record struct WorldLaunchOptions(
    bool TerrainTest, Size? TerrainSizeOverride, LevelGenMode GenMode, int Seed);

/// <param name="Parallel">Draw and materialise on several threads (only with <c>--fast</c>).</param>
/// <param name="BotSeed">Seed for the enemy tank's AI; null when the simulation is not deterministic.</param>
public sealed record BuiltWorld(World World, bool Parallel, int? BotSeed)
{
    public Size TerrainSize => World.Terrain.Size;
}

/// <summary>
/// Builds the world a session starts with: a generated match level, or the static terrain test patterns.
/// </summary>
public static class WorldFactory
{
    /// <summary>
    /// <c>--fast</c> (<see cref="LevelGenMode.Optimized"/>) asks for fast, parallel level generation, and today that
    /// also turns off simulation determinism: random seeds for the map, decorations and AI. Whether the two should be
    /// split is an open decision (see <c>docs/quality-plan.md</c>, "Decisions"); until it is made, this is the one
    /// place that ties them together.
    /// </summary>
    public static bool IsSimulationDeterministic(LevelGenMode genMode) => genMode == LevelGenMode.Deterministic;

    public static BuiltWorld Build(in WorldLaunchOptions options)
    {
        bool deterministic = IsSimulationDeterministic(options.GenMode);
        bool parallel = options.GenMode == LevelGenMode.Optimized;
        World world = options.TerrainTest
            ? BuildTerrainTest(options.TerrainSizeOverride ?? TerrainTestPatternGenerator.DefaultSize)
            : BuildMatch(options.TerrainSizeOverride ?? DesktopScreenTweaks.RenderSurfaceSize,
                options.GenMode, options.Seed, deterministic, parallel);
        return new BuiltWorld(world, parallel, BotSeed: deterministic ? options.Seed + 2 : null);
    }

    private static World BuildTerrainTest(Size size)
    {
        Console.WriteLine($"[TerrainTest] Generating test patterns {size.X}x{size.Y}...");
        var testTerrain = TerrainTestPatternGenerator.Generate(size);

        var world = new World(size, deterministicSimulation: true, simulationSeed: Game.DefaultSeed,
            enableTerrainRegrowth: false);
        world.Profile.ReportSink = static line => Console.WriteLine(line);
        // Copy pixels directly -- test patterns use final pixel values, skip materialization.
        for (int i = 0; i < testTerrain.Size.Area; i++)
        {
            world.Terrain[i] = testTerrain[i];
            // Propagate scorch intensity from the pattern grid.
            world.Terrain.SetScorchIntensity(i, testTerrain.GetScorchIntensity(i));
        }
        Console.WriteLine("[TerrainTest] Static terrain loaded. No simulation, WASD to pan camera.");
        return world;
    }

    private static World BuildMatch(Size size, LevelGenMode genMode, int seed, bool deterministic, bool parallel)
    {
        var modeLabel = parallel ? "optimized" : "deterministic";
        Console.WriteLine($"Generating terrain {size.X}x{size.Y} ({modeLabel}, seed={(deterministic ? seed.ToString(CultureInfo.InvariantCulture) : "random")})...");
        var genWatch = Stopwatch.StartNew();
        var generator = new ToastGenerator();
        var (terrain, spawns) = generator.Generate(size, deterministic ? seed : null, genMode);
        Console.WriteLine($"Level generated in {genWatch.Elapsed.TotalMilliseconds:F0} ms, {spawns.Length} spawns");

        var world = new World(
            size,
            deterministicSimulation: deterministic,
            simulationSeed: deterministic ? seed : 0,
            hotRockPocketCount: Tweaks.World.HotRockPocketCount);
        world.Profile.ReportSink = static line => Console.WriteLine(line);
        world.Initialize(terrain, spawns, decorationSeed: deterministic ? seed + 1 : null, parallelMaterialize: parallel);
        if (world.HotRockPockets.Count > 0)
            Console.WriteLine($"[HotRock] {world.HotRockPockets.Count} pocket(s) at " +
                string.Join(", ", world.HotRockPockets.Select(p => $"({p.X},{p.Y})")));
        return world;
    }
}
