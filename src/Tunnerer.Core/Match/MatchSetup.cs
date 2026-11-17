namespace Tunnerer.Core.Match;

using Tunnerer.Core.Config;
using Tunnerer.Core.LevelGen;
using Tunnerer.Core.Types;

/// <summary>
/// How a generated match is built from its seed: the level, the world and the seeds derived from the match seed.
/// Every host that starts a match (the desktop game, a network match, the browser) builds it here, so the same seed
/// and options give the same world everywhere.
/// </summary>
public static class MatchSetup
{
    /// <summary>
    /// Generates the level for <paramref name="seed"/> and returns the world initialised on it.
    /// <paramref name="configure"/> runs on the new world before it is initialised (profiling, audits).
    /// </summary>
    public static World Build(
        Size size,
        int seed,
        MatchRules rules,
        int players = Tweaks.LevelGen.DefaultPlayers,
        LevelGenMode genMode = LevelGenMode.Deterministic,
        SimulationSettings? settings = null,
        bool parallel = false,
        Action<World>? configure = null)
    {
        var (terrain, spawns) = new ToastGenerator().Generate(size, seed, genMode, players);
        var world = new World(size, simulationSeed: seed, rules, settings);
        configure?.Invoke(world);
        world.Initialize(terrain, spawns, decorationSeed: DecorationSeed(seed), parallelMaterialize: parallel);
        return world;
    }

    /// <summary>Seed for the AI of the tank in <paramref name="slot"/> of a match with <paramref name="matchSeed"/>.</summary>
    public static int BotSeed(int matchSeed, int slot) => unchecked(matchSeed + 1 + slot);

    private static int DecorationSeed(int matchSeed) => unchecked(matchSeed + 1);
}
