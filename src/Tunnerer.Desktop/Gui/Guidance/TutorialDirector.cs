namespace Tunnerer.Desktop.Gui.Guidance;

using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Types;

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
        float ReadSeconds = 0f);

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
    public int StepCount => _steps.Length;
    public int StepIndex => _index;

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
                "The teal crystal is emberite ore, what everything is paid with. Drive into it to mine it into your hold (MATERIALS). The hold is small: bring it home.",
                static (_, w, p) => p.OreHeld >= Tweaks.Ore.RelayCost || OwnsAny(w, p, MachineType.Relay),
                Progress: static (_, p) => Ratio("Ore", p.OreHeld, Tweaks.Ore.RelayCost)),

            new("Mind your temperature",
                "Digging heats the tank (TEMP). Above 100° it burns your shield. Drive back into your base: it cools you down and banks your ore.",
                static (t, w, p) => t._unloadedHaul || OwnsAny(w, p, MachineType.Relay),
                Target: static (_, p) => p.Base?.Position),

            new("Build a relay",
                $"Press E to pick up a relay ({Tweaks.Ore.RelayCost} ore; inside your network the base store pays). Drive out of the base and press E again where the line to your network shows green.",
                static (_, w, p) => OwnsPowered(w, p, MachineType.Relay),
                Progress: static (w, p) => p.IsCarryingMachine ? ("Carrying it: find a spot with a green line, then press E", 1f)
                    : Ratio("Ore you can spend", p.QuoteBuild(MachineType.Relay).Stock, Tweaks.Ore.RelayCost)),

            new("Stay on your network",
                "The pale circles are your network. Inside them the links pull heat out of your tank and your hold unloads into the base. Outside, the OFF NETWORK clock runs: get back before it reaches 0, or plant another relay to reach further.",
                static (t, _, _) => t._cameBackCovered,
                ReadSeconds: 14f),

            new("Turn hot rock into power",
                "Red rock is hot. Plant a relay near it (its line must reach your network). It pumps the heat home, and your base engine turns it into charge.",
                static (t, _, _) => t._engineRan,
                Progress: static (_, p) => p.Base is { } b ? ($"Engine +{b.EngineOutputPerTick * Tweaks.Perf.TargetFps / 1000.0:0.0}k charge/s", b.EngineOutputPerTick > 1.0 ? 1f : 0f) : null,
                Target: static (w, p) => Nearest(w.HotRockPockets, p.Base?.Position ?? p.Position)),

            new("Build a harvester",
                $"Harvesters mine emberite into your base store on their own, while they stand inside your network. Press R to build one ({Tweaks.Ore.HarvesterCost} ore) and plant it near ore, inside your network.",
                static (_, w, p) => OwnsAny(w, p, MachineType.Harvester),
                Progress: static (w, p) => p.IsCarryingMachine && p.CarriedMachineType == MachineType.Harvester
                    ? ("Carrying it: drive near ore, inside your network, and press R", 1f)
                    : Ratio("Ore you can spend", p.QuoteBuild(MachineType.Harvester).Stock, Tweaks.Ore.HarvesterCost)),

            new("Keep the harvester cool",
                "Mining heats the ground, and a harvester stalls when its ring turns orange. Plant a linked relay close to it to pump that heat away.",
                static (_, w, p) => HarvesterHasRelayNearby(w, p),
                Target: static (w, p) => FirstHarvester(w, p)),

            new("Power the harvester",
                $"Harvesters run on a battery (the bar above them). Press T to build a pylon ({Tweaks.Ore.PylonCost} ore) where it can see the harvester: it recharges batteries from your base's charge.",
                static (_, w, p) => OwnsPowered(w, p, MachineType.Pylon),
                Target: static (w, p) => FirstHarvester(w, p)),

            new("Upgrade",
                "Dock and press 1-6 to spend the base ore store: radiator, network reach, engine and insulation for the base, a bigger cargo hold and a longer radar for your tank.",
                static (_, _, p) => p.Base is { } b && Enumerable.Range(0, BaseUpgrades.Count).Any(i => b.Upgrades.Level((BaseUpgrade)i) > 0),
                Progress: static (_, p) => p.Base is { } b
                    ? Ratio("Base ore store", b.Store.Ore, Tweaks.Upgrades.LevelCosts[0])
                    : null,
                Target: static (_, p) => p.Base?.Position),

            new("Cool off at the frost",
                "Blue rock is frost. Beside it a hot tank cools fast, a good stop on a long dig. Stay too long and the cold slows you down, then cracks your shield.",
                static (t, _, _) => t._nearFrost,
                Target: static (w, p) => Nearest(w.FrostRockPockets, p.Position),
                ReadSeconds: 16f),

            new("Tap the core",
                "Chain relays to the hot core at the centre. Relays beside it must make the goal rate of charge, held for a minute. More relays there, a bigger radiator and a better engine all help.",
                static (_, w, p) => w.CorePocket == null || p.Base is { HasTappedCore: true },
                Progress: static (w, p) => p.Base is { } b && w.CorePocket != null
                    ? ($"Core tap {b.CoreTapPerTick * Tweaks.Perf.TargetFps / 1000.0:0.0}k / {Tweaks.Goal.GoalChargePerSecond / 1000.0:0.0}k charge/s, held {b.CoreHoldSeconds:0} / {Tweaks.Goal.GoalHoldSeconds:0} s",
                        (float)Math.Clamp(b.CoreHoldSeconds / Tweaks.Goal.GoalHoldSeconds, 0.0, 1.0))
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
        return new TutorialPrompt(_index + 1, _steps.Length, current.Title, current.Body,
            progress?.Text, progress?.Fraction ?? 0f, current.Target?.Invoke(world, player));
    }

    /// <summary>Skips the rest of the tutorial.</summary>
    public void Skip() => _index = _steps.Length;

    private void Observe(World world, Tank player)
    {
        if (player.Base is not { } home || player.IsDead)
            return;
        bool docked = DockedAtHome(world, player);
        if (!docked)
            _hasLeftBase = true;
        // A real haul: the tank came home with at least a relay's worth of ore and docked.
        if (!docked && player.OreHeld >= Tweaks.Ore.RelayCost)
            _carriedHaul = true;
        if (docked && _carriedHaul)
            _unloadedHaul = true;
        if (player.IsOffNetwork)
            _wasOffNetwork = true;
        else if (_wasOffNetwork && (player.Coverage.Covered || DockedAtHome(world, player)))
            _cameBackCovered = true;
        if (home.EngineOutputPerTick > 1.0)
            _engineRan = true;
        foreach (var frost in world.FrostRockPockets)
            if (Position.DistanceSquared(frost, player.Position) <= 14 * 14)
                _nearFrost = true;
    }

    private static (string, float) Ratio(string label, int have, int need) =>
        ($"{label} {Math.Min(have, need)} / {need}", Math.Clamp(have / (float)need, 0f, 1f));

    private static bool DockedAtHome(World world, Tank player) =>
        player.Base != null && world.TankBases.CheckBaseCollision(player.Position) == player.Base;

    private static IEnumerable<Machine> Owned(World world, Tank player, MachineType type) =>
        world.Machines.Machines.Where(m => m.IsAlive && m.State == MachineState.Planted && m.Type == type &&
                                           m.OwnerBase == player.Base && player.Base != null);

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
