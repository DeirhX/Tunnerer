namespace Tunnerer.Browser.Client;

using Tunnerer.Core;
using Tunnerer.Core.Bots;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Tanks;
using Tunnerer.Core.Input;
using Tunnerer.Core.Match;
using Tunnerer.Core.Sensing;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Audio;
using Tunnerer.Desktop.Gui.Guidance;

/// <summary>The browser's local match. All game rules and bot decisions belong to Core.</summary>
public sealed class BrowserSession
{
    /// <summary>The map of a normal match.</summary>
    public static Size DefaultMapSize => new(1500, 750);

    /// <summary>The largest map the page asks for (<c>?map=WxH</c>); WebGL 2 promises textures of 2048 and more.</summary>
    public static Size LargestMapSize => new(4096, 4096);
    private const int BotSlot = 1;
    private readonly BotTankAI _bot;
    private readonly SoundDirector _soundDirector;

    /// <param name="seed">The world's seed.</param>
    /// <param name="heatWorkers">
    /// How many threads the heat solver may use. One is the single-thread runtime, where it must not split its work;
    /// more need the threaded runtime (docs/browser.md). The world comes out the same either way.
    /// </param>
    /// <param name="mapSize">The map; null for <see cref="DefaultMapSize"/>. Kept within 320x200 and <see cref="LargestMapSize"/>.</param>
    /// <param name="machines">
    /// For measuring: this many machines planted in networks grown from every base before play starts
    /// (<see cref="BrowserStress"/>). Zero in a normal match.
    /// </param>
    public BrowserSession(int seed, int heatWorkers = 1, Size? mapSize = null, int machines = 0)
    {
        Seed = seed;
        var size = mapSize ?? DefaultMapSize;
        MapSize = new Size(Math.Clamp(size.X, 320, LargestMapSize.X), Math.Clamp(size.Y, 200, LargestMapSize.Y));
        var settings = SimulationSettings.FromTweaks() with { ThermalMaxWorkers = Math.Max(1, heatWorkers) };
        if (heatWorkers <= 1)
            settings = settings with { ThermalParallelTileThreshold = int.MaxValue };
        World = MatchSetup.Build(MapSize, seed, MatchRules.Standard with { CoreTapWins = true }, settings: settings);
        World.SetBotControlled(BotSlot);
        _bot = new BotTankAI(MatchSetup.BotSeed(seed, BotSlot));
        Vision = World.TrackVision(Player);
        Radar = World.TrackRadar(Player);
        if (machines > 0)
            Machines = BrowserStress.GrowNetworks(World, machines);
        Scene = new BrowserScene(this);
        Guidance.Update(World, Player, HudSeconds, offerThermalView: false);
        _soundDirector = new SoundDirector(Sounds);
    }

    public int Seed { get; }

    public Size MapSize { get; }

    /// <summary>How many machines <see cref="BrowserStress"/> planted before play; zero in a normal match.</summary>
    public int Machines { get; }
    public World World { get; }
    public Tank Player => World.TankList.Tanks[0];
    public VisionMap Vision { get; }
    public Radar Radar { get; }
    public BrowserScene Scene { get; }

    /// <summary>What the desktop would play after each step (<see cref="SoundDirector"/>), for the page to play.</summary>
    public SoundRecorder Sounds { get; } = new();

    /// <summary>The tutorial, toasts, hint and warning, as the desktop HUD chooses them; updated after each step.</summary>
    public PlayerGuidance Guidance { get; } = new();

    /// <summary>
    /// The HUD's clock: the match's own time, so that toasts and tutorial steps last as long as on the desktop and
    /// stand still while the game is paused.
    /// </summary>
    public float HudSeconds => World.AdvanceCount / (float)Tweaks.Perf.TargetFps;

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
        _soundDirector.AfterStep(World, localSlot: 0);
        Guidance.AfterStep(World, Player);
        // The browser has no thermal view to tell the player about.
        Guidance.Update(World, Player, HudSeconds, offerThermalView: false);
        Scene.CaptureStep();
    }
}
