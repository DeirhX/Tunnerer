namespace Emberward.Desktop.Gui.Guidance;

using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Types;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Entities.Bases;
using Emberward.Desktop.Presentation;

/// <summary>What the tutorial asks of the player right now.</summary>
/// <param name="Progress">Optional progress line (e.g. "Dirt 87 / 150") and fill fraction.</param>
/// <param name="Target">Optional world cell the HUD points at.</param>
public readonly record struct TutorialPrompt(
    int Number, int Count, string Title, string Body, string? Progress, float ProgressFraction, Position? Target);

/// <summary>
/// The tutorial: an ordered list of next logical steps, from leaving the base to keeping a harvester cooled and
/// powered. Each step knows when it is done by reading the world, so a player who does things early or out of order
/// simply skips ahead; nothing is forced. Steps that only explain something finish once the player has seen them for
/// a moment or has met the thing they describe.
/// </summary>
public sealed class TutorialDirector
{
    private sealed record Step(
        string Title,
        string Body,
        Func<TutorialDirector, World, Tank, bool> IsDone,
        Func<World, Tank, (string Text, float Fraction)?>? Progress = null,
        Func<World, Tank, Position?>? Target = null,
        float ReadSeconds = 0f,
        string? EnergyBody = null);

    private readonly Step[] _steps;
    private int _index;
    private float _shownSince = float.NaN;

    // Things the tutorial has seen happen, for steps about experiences rather than state.
    private bool _hasLeftBase;
    private bool _wasOffNetwork;
    private bool _cameBackCovered;
    private bool _engineRan;
    private bool _nearFrost;
    private bool _carriedHaul;
    private bool _unloadedHaul;

    public bool IsComplete => _index >= _steps.Length;

    /// <summary>Seconds the current step has been shown.</summary>
    private float ShownFor(float now) => float.IsNaN(_shownSince) ? 0f : now - _shownSince;

    public TutorialDirector()
    {
        _steps =
        [
            new("Leave the base",
                "Your goal is to tap the hot core at the centre of the map. First, get out: drive with W A S D through the gap in the bottom wall of your base.",
                static (t, _, _) => t._hasLeftBase,
                Target: static (_, p) => p.Base is { } b ? b.Position + new Offset(0, Tweaks.Base.BaseSize / 2) : null),

            new("Mine emberite",
                "The gold crystal is emberite ore, what everything is paid with. Drive into it to mine it into your hold (MATERIALS). The hold is small: bring it home.",
                static (_, w, p) => p.OreHeld >= Cost(MachineType.Relay) || OwnsAny(w, p, MachineType.Relay),
                Progress: static (_, p) => Ratio("Ore", p.OreHeld, Cost(MachineType.Relay))),

            new("Mind your temperature",
                "Digging heats the tank (TEMP). Above 100° it burns your shield. Drive back into your base: it cools you down and banks your ore.",
                static (t, w, p) => t._unloadedHaul || OwnsAny(w, p, MachineType.Relay),
                Target: static (_, p) => p.Base?.Position),

            new("Build a relay",
                $"Press {Key(MachineType.Relay)} to pick up a relay ({Cost(MachineType.Relay)} ore; inside your network the base store pays). Drive out of the base and press {Key(MachineType.Relay)} again where the line to your network shows green.",
                static (_, w, p) => OwnsPowered(w, p, MachineType.Relay),
                Progress: static (w, p) => p.IsCarryingMachine ? ($"Carrying it: find a spot with a green line, then press {Key(MachineType.Relay)}", 1f)
                    : Ratio("Ore you can spend", p.QuoteBuild(MachineType.Relay).Stock, Cost(MachineType.Relay))),

            new("Stay on your network",
                "The pale circles are your network. Inside them the links pull heat out of your tank and your hold unloads into the base. Outside, the OFF NETWORK clock runs: get back before it reaches 0, or plant another relay to reach further.",
                static (t, _, _) => t._cameBackCovered,
                ReadSeconds: 14f),

            new("Turn hot rock into power",
                "Red rock is hot. Plant a relay near it (its line must reach your network). It pumps the heat home, and your base engine turns it into charge.",
                static (t, _, _) => t._engineRan,
                Progress: static (_, p) => p.Base is { } b ? ($"Engine +{b.EngineOutputPerSecond / 1000.0:0.0}k charge/s", b.IsEngineRunning ? 1f : 0f) : null,
                Target: static (w, p) => Nearest(w.HotRockPockets, p.Base?.Position ?? p.Position)),

            new("Build a harvester",
                $"Harvesters mine emberite into your base store on their own, while they stand inside your network. Press {Key(MachineType.Harvester)} to build one ({Cost(MachineType.Harvester)} ore) and plant it near ore, inside your network.",
                static (_, w, p) => OwnsAny(w, p, MachineType.Harvester),
                Progress: static (w, p) => p.IsCarryingMachine && p.CarriedMachineType == MachineType.Harvester
                    ? ($"Carrying it: drive near ore, inside your network, and press {Key(MachineType.Harvester)}", 1f)
                    : Ratio("Ore you can spend", p.QuoteBuild(MachineType.Harvester).Stock, Cost(MachineType.Harvester))),

            new("Keep the harvester cool",
                "Mining heats the ground, and a harvester stalls when its ring turns orange. Plant a linked relay close to it to pump that heat away.",
                static (_, w, p) => HarvesterHasRelayNearby(w, p),
                Target: static (w, p) => FirstHarvester(w, p)),

            new("Power the harvester",
                "Harvesters run on your base's charge (the Charge bar in the base panel) wherever your network covers them. Relays make that charge from warm ground: keep one by hot rock so the store never runs dry.",
                static (_, w, p) => w.Rules.EnergyNetwork
                    ? OwnsAny(w, p, MachineType.Generator)
                    : Owned(w, p, MachineType.Harvester).Any(h => !MachineBehaviors.IsOutOfCharge(h)),
                Target: static (w, p) => w.Rules.EnergyNetwork
                    ? Nearest(w.HotRockPockets, p.Position) : FirstHarvester(w, p),
                EnergyBody: $"Harvesters draw charge through the network. Press {Key(MachineType.Generator)} to build a generator ({Cost(MachineType.Generator)} ore) and plant it beside hot ground. A generator can power nearby machines through their links even when cut off from the base."),

            new("Upgrade",
                "Dock and press 1-7 to spend the base ore store: radiator, network reach, engine, insulation and sensors for the base, a bigger cargo hold and a longer radar for your tank.",
                static (_, _, p) => p.Base is { } b && Enumerable.Range(0, BaseUpgrades.Count).Any(i => b.Upgrades.Level((BaseUpgrade)i) > 0),
                Progress: static (_, p) => p.Base is { } b
                    ? Ratio("Base ore store", b.Store.Ore, Tweaks.Upgrades.LevelCosts[0])
                    : null,
                Target: static (_, p) => p.Base?.Position),

            new("Upgrade a relay",
                "Stop beside one of your relays and press F (or tap it): its panel offers Tap (pumps more heat, so more charge, and runs hotter), Sensor (sees further) or Conduit (a bigger capacitor). Press 1-3 to buy from the base ore store; a relay keeps the path it takes first.",
                static (_, w, p) => !w.Rules.MachineUpgrades || Owned(w, p, MachineType.Relay).Any(m => w.MachineUpgradeOf(m).Level > 0),
                Progress: static (_, p) => p.Base is { } b
                    ? Ratio("Base ore store", b.Store.Ore, MachineUpgrades.CostOfLevel(0))
                    : null,
                Target: static (w, p) => w.MachineUpgradeOfferFor(p)?.Machine.Position
                    ?? Owned(w, p, MachineType.Relay).Select(m => (Position?)m.Position).FirstOrDefault()),

            new("Defend with a turret",
                $"With the energy network, press {Key(MachineType.Turret)} to build a turret ({Cost(MachineType.Turret)} ore) and plant it inside your network's coverage. While its island feeds it, it fires at enemy tanks within {Tweaks.Turret.Range} cells that it can see. Each shot heats the ground around it, and a hot turret fires more slowly: a relay nearby keeps it cool. F beside it offers Rapid or Long.",
                static (_, w, p) => !w.Rules.EnergyNetwork || OwnsAny(w, p, MachineType.Turret),
                Progress: static (_, p) => p.Base is { } b
                    ? Ratio("Base ore store", b.Store.Ore, Cost(MachineType.Turret))
                    : null,
                Target: static (w, p) => Owned(w, p, MachineType.Relay).Select(m => (Position?)m.Position).FirstOrDefault()),

            new("Cool off at the frost",
                "Blue rock is frost. Beside it a hot tank cools fast, a good stop on a long dig. Stay too long and the cold slows you down, then cracks your shield.",
                static (t, _, _) => t._nearFrost,
                Target: static (w, p) => Nearest(w.FrostRockPockets, p.Position),
                ReadSeconds: 16f),

            new("Tap the core",
                "Chain relays to the hot core at the centre. Relays beside it must make the goal rate of charge, held for a minute. More relays there, a bigger radiator and a better engine all help.",
                static (_, w, p) => w.CorePocket == null || w.CoreTapOf(p.Base) is { HasTapped: true },
                Progress: static (w, p) => w.CoreTapOf(p.Base) is { } b
                    ? ($"Core tap {b.TapPerSecond / 1000.0:0.0}k / {Tweaks.Goal.GoalChargePerSecond / 1000.0:0.0}k charge/s, held {b.HoldSeconds:0} / {Tweaks.Goal.GoalHoldSeconds:0} s",
                        b.HoldFraction)
                    : null,
                Target: static (w, _) => w.CorePocket),
        ];
    }

    /// <summary>Advances past every step that is done and returns the step to show, or null when the tutorial is over.</summary>
    public TutorialPrompt? Update(World world, Tank player, float now)
    {
        Observe(world, player);
        while (_index < _steps.Length)
        {
            if (float.IsNaN(_shownSince))
                _shownSince = now;
            var step = _steps[_index];
            bool done = step.IsDone(this, world, player) ||
                        (step.ReadSeconds > 0f && ShownFor(now) >= step.ReadSeconds);
            if (!done)
                break;
            _index++;
            _shownSince = float.NaN;
        }

        if (IsComplete)
            return null;
        var current = _steps[_index];
        var progress = current.Progress?.Invoke(world, player);
        string body = world.Rules.EnergyNetwork ? current.EnergyBody ?? current.Body : current.Body;
        return new TutorialPrompt(_index + 1, _steps.Length, current.Title, body,
            progress?.Text, progress?.Fraction ?? 0f, current.Target?.Invoke(world, player));
    }

    /// <summary>Skips the rest of the tutorial.</summary>
    public void Skip() => _index = _steps.Length;

    private void Observe(World world, Tank player)
    {
        if (player.Base is not { } home || player.IsDead)
            return;
        bool docked = player.IsDockedAtOwnBase;
        if (!docked)
            _hasLeftBase = true;
        // A real haul: the tank came home with at least a relay's worth of ore and docked.
        if (!docked && player.OreHeld >= Cost(MachineType.Relay))
            _carriedHaul = true;
        if (docked && _carriedHaul)
            _unloadedHaul = true;
        if (player.IsOffNetwork)
            _wasOffNetwork = true;
        else if (_wasOffNetwork && (player.Coverage.Covered || player.IsDockedAtOwnBase))
            _cameBackCovered = true;
        if (home.IsEngineRunning)
            _engineRan = true;
        foreach (var frost in world.FrostRockPockets)
            if (Position.DistanceSquared(frost, player.Position) <= 14 * 14)
                _nearFrost = true;
    }

    private static int Cost(MachineType type) => MachineBehaviors.Get(type).OreCost;

    private static string Key(MachineType type) => MachinePresentations.Get(type).KeyLabel;

    private static (string, float) Ratio(string label, int have, int need) =>
        ($"{label} {Math.Min(have, need)} / {need}", Math.Clamp(have / (float)need, 0f, 1f));

    private static IEnumerable<Machine> Owned(World world, Tank player, MachineType type) =>
        world.Machines.Machines.Where(m => player.Base != null && m.StandsFor(player.Base) && m.Type == type);

    private static bool OwnsAny(World world, Tank player, MachineType type) => Owned(world, player, type).Any();

    private static bool OwnsPowered(World world, Tank player, MachineType type) =>
        Owned(world, player, type).Any(m => m.LinkPoint?.IsPowered == true);

    private static Position? FirstHarvester(World world, Tank player) =>
        Owned(world, player, MachineType.Harvester).Select(m => (Position?)m.Position).FirstOrDefault();

    private static bool HarvesterHasRelayNearby(World world, Tank player)
    {
        int reach = Tweaks.Machine.RelayCoolingRadius + Tweaks.Machine.HarvesterHeatSenseRadius;
        var relays = Owned(world, player, MachineType.Relay).Where(r => r.LinkPoint?.IsPowered == true).ToList();
        return Owned(world, player, MachineType.Harvester).Any(h =>
            relays.Any(r => Math.Abs(r.Position.X - h.Position.X) <= reach && Math.Abs(r.Position.Y - h.Position.Y) <= reach));
    }

    private static Position? Nearest(IReadOnlyList<Position> points, Position from)
    {
        Position? best = null;
        int bestDist = int.MaxValue;
        foreach (var p in points)
        {
            int d = Position.DistanceSquared(p, from);
            if (d < bestDist) { bestDist = d; best = p; }
        }
        return best;
    }
}
