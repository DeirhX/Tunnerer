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

    public BrowserSession(int seed)
    {
        Seed = seed;
        // The browser runtime has one thread (docs/browser.md says why), so the heat solver is told not to split its work.
        var settings = SimulationSettings.FromTweaks() with
        {
            ThermalMaxWorkers = 1,
            ThermalParallelTileThreshold = int.MaxValue,
        };
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

    public void Advance(ControllerOutput input)
    {
        if (World.MatchOutcomeFor(Player) != MatchOutcome.InProgress)
            return;
        var tanks = World.TankList.Tanks;
        World.Advance(i => i == 0 ? input : _bot.GetInput(tanks[i], Player, World));
        Scene.CaptureStep();
    }
}
