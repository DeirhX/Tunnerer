namespace Emberward.Browser.Client;

using Emberward.Core;
using Emberward.Core.Bots;
using Emberward.Core.Config;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Input;
using Emberward.Core.Match;
using Emberward.Core.Sensing;
using Emberward.Core.LevelGen;
using Emberward.Core.Types;
using Emberward.Desktop;
using Emberward.Desktop.Audio;
using Emberward.Desktop.Gui;
using Emberward.Desktop.Gui.Guidance;
using Emberward.Protocol;

/// <summary>
/// The browser's match: a local one against a bot, or this player's world in a network match
/// (<see cref="BrowserNetMatch"/>). All game rules and bot decisions belong to Core.
/// </summary>
public sealed class BrowserSession
{
    /// <summary>The map of a normal match.</summary>
    public static Size DefaultMapSize => new(1500, 750);

    /// <summary>The largest map the page asks for (<c>?map=WxH</c>); WebGL 2 promises textures of 2048 and more.</summary>
    public static Size LargestMapSize => new(4096, 4096);
    private const int BotSlot = 1;
    private readonly BotTankAI?[] _bots = [];
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
    /// <param name="energyNetwork">
    /// The match runs the energy network (<see cref="MatchRules.EnergyNetwork"/>, the page's <c>?energy</c>), off in
    /// <see cref="MatchRules.Standard"/>. Only a local match can ask for it.
    /// </param>
    public BrowserSession(int seed, int heatWorkers = 1, Size? mapSize = null, int machines = 0, bool energyNetwork = false)
        : this(seed, ClampMap(mapSize ?? DefaultMapSize), Tweaks.LevelGen.DefaultPlayers, LevelGenMode.Deterministic,
            heatWorkers, localSlot: 0, localBot: true, machines, energyNetwork)
    {
    }

    /// <summary>
    /// The machine upgrade panel (F, or a tap on the lit machine), with the closing rules the desktop has. UI state
    /// only: what reaches the simulation is the upgrade key sent while it is open.
    /// </summary>
    public MachineUpgradePanel MachineUpgrades { get; } = new();

    /// <summary>
    /// The page's input with the machine upgrade panel applied: <paramref name="togglePanel"/> (F or a tap on the lit
    /// machine) opens or closes it, and while it is open the number keys buy for the machine next to the tank
    /// (<see cref="ControllerOutput.UpgradeMachine"/>) instead of base upgrades. <paramref name="forMachine"/> marks a
    /// key the page queued while the panel was open (or a click on one of its choices): it stays a machine purchase
    /// even when an earlier purchase has closed the panel by the time it is sent, so it never buys a base upgrade.
    /// </summary>
    public ControllerOutput WithMachineUpgradePanel(ControllerOutput input, bool togglePanel, bool forMachine = false)
    {
        MachineUpgrades.Update(World, Player);
        if (togglePanel)
            MachineUpgrades.Toggle(World, Player);
        input.UpgradeMachine = input.Upgrade != 0 && (forMachine || MachineUpgrades.IsOpen);
        return input;
    }

    /// <param name="localBot">A local match: the bot drives <see cref="BotSlot"/>.</param>
    private BrowserSession(int seed, Size mapSize, int players, LevelGenMode genMode, int heatWorkers, int localSlot,
        bool localBot = false, int machines = 0, bool energyNetwork = false)
    {
        Seed = seed;
        MapSize = mapSize;
        var settings = SimulationSettings.FromTweaks() with { ThermalMaxWorkers = Math.Max(1, heatWorkers) };
        if (heatWorkers <= 1)
            settings = settings with { ThermalParallelTileThreshold = int.MaxValue };
        // A network match is a race, as on the desktop: the first base to tap the core wins.
        World = MatchSetup.Build(MapSize, seed,
            MatchRules.Standard with { CoreTapWins = true, EnergyNetwork = energyNetwork, MachineUpgrades = energyNetwork }, players, genMode,
            settings: settings);
        LocalSlot = localSlot;
        if (localBot)
        {
            World.SetBotControlled(BotSlot, botControlled: true);
            _bots = new BotTankAI?[World.TankList.Count];
            _bots[BotSlot] = new BotTankAI(MatchSetup.BotSeed(seed, BotSlot));
        }
        Vision = World.TrackVision(Player);
        Radar = World.TrackRadar(Player);
        if (machines > 0)
            Machines = BrowserStress.GrowNetworks(World, machines);
        Scene = new BrowserScene(this);
        Guidance.Update(World, Player, HudSeconds, offerThermalView: true);
        _soundDirector = new SoundDirector(Sounds);
    }

    /// <summary>
    /// This player's world in a network match: the room's settings as the desktop builds them
    /// (<c>WorldFactory</c>), so that both compute the same world. Nothing is stepped; the bots come when the match
    /// starts (<see cref="NetSession.StartSeats"/>).
    /// </summary>
    /// <param name="localSlot">The slot the relay gave this player.</param>
    public static BrowserSession ForNetwork(MatchConfig config, int localSlot, int heatWorkers = 1)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (localSlot < 0 || localSlot >= config.Slots)
            throw new ArgumentOutOfRangeException(nameof(localSlot));
        return new BrowserSession(config.Seed, new Size(config.MapWidth, config.MapHeight), config.Slots,
            (LevelGenMode)config.GenMode, heatWorkers, localSlot);
    }

    private static Size ClampMap(Size size) =>
        new(Math.Clamp(size.X, 320, LargestMapSize.X), Math.Clamp(size.Y, 200, LargestMapSize.Y));

    public int Seed { get; }

    public Size MapSize { get; }

    /// <summary>How many machines <see cref="BrowserStress"/> planted before play; zero in a normal match.</summary>
    public int Machines { get; }
    public World World { get; }
    /// <summary>The tank this player drives: 0 in a local match, the relay's choice in a network one.</summary>
    public int LocalSlot { get; }

    public Tank Player => World.TankList.Tanks[LocalSlot];
    public VisionMap Vision { get; }
    public Radar Radar { get; }
    public BrowserScene Scene { get; }

    /// <summary>What the desktop would play after each step (<see cref="SoundDirector"/>), for the page to play.</summary>
    public SoundRecorder Sounds { get; } = new();

    /// <summary>The tutorial, toasts, hint and warning, as the desktop HUD chooses them; updated after each step.</summary>
    public PlayerGuidance Guidance { get; } = new();

    /// <summary>
    /// The thermal view (H), as on the desktop: the frame tints the ground by its heat, so that enemy tunnels glow.
    /// It changes only what is drawn, never the world.
    /// </summary>
    public bool ThermalView { get; set; }

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

    /// <summary>One step of the local match: the player's input, and the bot's.</summary>
    public void Advance(ControllerOutput input)
    {
        if (World.MatchOutcomeFor(Player) != MatchOutcome.InProgress)
            return;
        SlotStepper.Advance(World, _bots, _ => input);
        AfterStep(silent: false);
    }

    /// <summary>What follows every step, local or from the relay: sound, guidance and what the next frame draws.</summary>
    /// <param name="silent">No sound for this step: it is a tick from the past, replayed to catch up.</param>
    public void AfterStep(bool silent)
    {
        if (!silent)
            _soundDirector.AfterStep(World, LocalSlot);
        Guidance.AfterStep(World, Player);
        Guidance.Update(World, Player, HudSeconds, offerThermalView: !ThermalView);
        Scene.CaptureStep(silent);
    }
}
