namespace Tunnerer.Browser.Client;

using Tunnerer.Core;
using Tunnerer.Core.Bots;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Tanks;
using Tunnerer.Core.Input;
using Tunnerer.Core.LevelGen;
using Tunnerer.Core.Match;
using Tunnerer.Core.Sensing;
using Tunnerer.Core.Types;

/// <summary>The browser's local match. All game rules and bot decisions belong to Core.</summary>
public sealed class BrowserSession
{
    public static Size MapSize => new(1500, 750);
    private readonly BotTankAI _bot;

    /// <param name="seed">The world's seed.</param>
    /// <param name="heatWorkers">
    /// How many threads the heat solver may use. One is the single-thread runtime, where it must not split its work;
    /// more need the threaded runtime (docs/browser.md). The world comes out the same either way.
    /// </param>
    public BrowserSession(int seed, int heatWorkers = 1)
    {
        Seed = seed;
        var settings = SimulationSettings.FromTweaks() with { ThermalMaxWorkers = Math.Max(1, heatWorkers) };
        if (heatWorkers <= 1)
            settings = settings with { ThermalParallelTileThreshold = int.MaxValue };
        var (terrain, spawns) = new ToastGenerator().Generate(MapSize, seed, LevelGenMode.Deterministic, 2);
        World = new World(MapSize, simulationSeed: seed, rules: MatchRules.Standard with { CoreTapWins = true },
            simulationSettings: settings);
        World.Initialize(terrain, spawns, decorationSeed: unchecked(seed + 1));
        World.SetBotControlled(1);
        _bot = new BotTankAI(unchecked(seed + 2));
        Vision = World.TrackVision(Player);
        Radar = World.TrackRadar(Player);
        Scene = new BrowserScene(this);
    }

    public int Seed { get; }
    public World World { get; }
    public Tank Player => World.TankList.Tanks[0];
    public VisionMap Vision { get; }
    public Radar Radar { get; }
    public BrowserScene Scene { get; }

    /// <summary>
    /// One number for everything the simulation holds, the heat of every cell included. Two sessions with the same
    /// seed and inputs agree on it whichever runtime, and however many heat workers, stepped them.
    /// </summary>
    public ulong Fingerprint()
    {
        ulong hash = WorldStateHash.ComputeSync(World).Combined;
        var terrain = World.Terrain;
        for (int i = 0; i < MapSize.Area; i++)
        {
            hash = Fnv1a.Mix(hash, BitConverter.SingleToUInt32Bits(terrain.GetHeatTemperature(i)));
            hash = Fnv1a.Mix(hash, BitConverter.SingleToUInt32Bits(terrain.GetAirTemperature(i)));
        }
        return hash;
    }

    public void Advance(ControllerOutput input)
    {
        if (World.MatchOutcomeFor(Player) != MatchOutcome.InProgress)
            return;
        var tanks = World.TankList.Tanks;
        World.Advance(i => i == 0 ? input : _bot.GetInput(tanks[i], Player, World));
        Scene.CaptureStep();
    }
}
