namespace Emberward.Core.Entities.Machines;

using Emberward.Core.Config;
using Emberward.Core.Entities.Bases;
using Emberward.Core.Entities.Tanks;
using Emberward.Core.Input;
using Emberward.Core.Match;
using Emberward.Core.Network;
using Emberward.Core.Snapshots;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

/// <summary>
/// Per-machine upgrades (DESIGN.md §4.9), in a world whose rules run them (<see cref="MatchRules.MachineUpgrades"/>).
/// A tank buys the next level for the nearest of its own machines within <see cref="Tweaks.MachineUpgrades.Reach"/>
/// cells, paying from its base's ore store: the machine upgrade input (<see cref="ControllerOutput.UpgradeMachine"/>)
/// with the upgrade key n buys path n of <see cref="MachineBehavior.UpgradePaths"/>, on the press only. A machine's
/// first purchase fixes its path.
/// <para>
/// The effects go through the paths that already carry them: Tap's pump rate is asked by the relay's action
/// (<see cref="PumpPerTick"/>), Tap's idle draw and Conduit's capacitor are set on the energy network
/// (<see cref="EnergyNetwork.Resize"/>), which turns idle draw into heat at the machine, and Sensor's sight is asked by
/// vision (<see cref="SightBonus"/>). Cost follows change: nothing runs per tick but a tank's key, and a question about
/// a machine looks it up by halving.
/// </para>
/// </summary>
public sealed class MachineUpgradeSystem
{
    private readonly MachineList _machines;
    private readonly EnergyNetwork? _energy;
    private readonly ITerrainAccess _terrain;
    private readonly StepEventLog _events;
    private readonly MatchRules _rules;

    // One per upgraded machine, in machine sequence order (Find looks a machine up by halving).
    private readonly List<Entry> _entries = new();
    // Each tank's key state, by its colour (its spawn index); grown as tanks first act.
    private readonly List<TankKeys> _tanks = new();

    [NotSaved("scratch: filled and read within one question")]
    private readonly List<Machine> _near = new();

    public MachineUpgradeSystem(MachineList machines, EnergyNetwork? energy, ITerrainAccess terrain, StepEventLog events, MatchRules rules)
    {
        _machines = machines;
        _energy = energy;
        _terrain = terrain;
        _events = events;
        _rules = rules;
    }

    /// <summary>
    /// One tick of <paramref name="tank"/>'s machine upgrade key, from <see cref="Tank.Advance"/>: counts down its last
    /// refusal and buys on a press (the upgrade key changed while the machine upgrade input is held).
    /// </summary>
    internal void Advance(Tank tank, in ControllerOutput input)
    {
        int slot = SlotOf(tank);
        TankKeys keys = _tanks[slot];
        if (keys.RefusedTicks > 0)
            keys.RefusedTicks--;
        bool pressed = input.UpgradeMachine && input.Upgrade != 0 && input.Upgrade != keys.Held;
        keys.Held = input.Upgrade;
        if (pressed)
        {
            MachineUpgradeRefusal refusal = TryBuy(tank, input.Upgrade - 1);
            keys.LastRefusal = refusal;
            keys.RefusedTicks = refusal != MachineUpgradeRefusal.None ? Tweaks.MachineUpgrades.RefusalTicks : 0;
        }
        _tanks[slot] = keys;
    }

    /// <summary>
    /// The machine <paramref name="tank"/> would upgrade now: of its base's standing machines that have upgrade paths,
    /// the nearest within reach (a circle), the earliest built on a tie. Null when there is none.
    /// </summary>
    public Machine? TargetOf(Tank tank)
    {
        if (tank.IsDead || tank.Base is not { IsDestroyed: false } home)
            return null;
        int reach = Tweaks.MachineUpgrades.Reach;
        _machines.CollectNear(tank.Position, reach, _near, inListOrder: true);
        Machine? best = null;
        long bestDistance = (long)reach * reach;
        foreach (var machine in _near)
        {
            if (!machine.StandsFor(home) || machine.Behavior.UpgradePaths.IsEmpty)
                continue;
            long dx = machine.Position.X - tank.Position.X, dy = machine.Position.Y - tank.Position.Y;
            long distance = dx * dx + dy * dy;
            // Strictly nearer only: the list is in build order, so the earliest built keeps a tie.
            if (distance < bestDistance || (best == null && distance == bestDistance))
            {
                best = machine;
                bestDistance = distance;
            }
        }
        _near.Clear();
        return best;
    }

    /// <summary>What <paramref name="tank"/> could buy for <see cref="TargetOf"/>, or null when nothing is in reach.</summary>
    public MachineUpgradeOffer? OfferFor(Tank tank)
    {
        if (TargetOf(tank) is not Machine machine)
            return null;
        MachineUpgradeLevel upgrade = LevelOf(machine);
        int cost = upgrade.IsMaxed ? 0 : MachineUpgrades.CostOfLevel(upgrade.Level);
        bool canAfford = !upgrade.IsMaxed && tank.Base!.Store.CanPay(cost);
        return new MachineUpgradeOffer(machine, upgrade, cost, canAfford);
    }

    /// <summary>Why <paramref name="tank"/>'s last machine upgrade press bought nothing, and for how many more ticks to say so.</summary>
    public (MachineUpgradeRefusal Refusal, int Ticks) RefusalOf(Tank tank)
    {
        int slot = tank.Color;
        if (slot < 0 || slot >= _tanks.Count)
            return (MachineUpgradeRefusal.None, 0);
        TankKeys keys = _tanks[slot];
        return keys.RefusedTicks > 0 ? (keys.LastRefusal, keys.RefusedTicks) : (MachineUpgradeRefusal.None, 0);
    }

    /// <summary>The path <paramref name="machine"/> took and its level on it; level 0 and no path before any purchase.</summary>
    public MachineUpgradeLevel LevelOf(Machine machine) =>
        Find(machine) is Entry entry ? new MachineUpgradeLevel(entry.Path, entry.Level) : default;

    /// <summary>The heat <paramref name="relay"/> pumps per tick, its Tap levels included.</summary>
    public double PumpPerTick(Machine relay) => MachineUpgrades.PumpPerTick(LevelOf(relay).LevelOf(MachineUpgradePath.Tap));

    /// <summary>Extra cells <paramref name="machine"/> sees for its Sensor levels.</summary>
    public int SightBonus(Machine machine) => MachineUpgrades.SightBonus(LevelOf(machine).LevelOf(MachineUpgradePath.Sensor));

    /// <summary>A machine was wrecked: its upgrade goes with it.</summary>
    internal void Remove(Machine machine)
    {
        int index = IndexOf(machine);
        if (index >= 0)
            _entries.RemoveAt(index);
    }

    /// <summary>Mixes every machine's upgrade, for <see cref="WorldStateHash.Compute"/>.</summary>
    internal ulong MixState(ulong hash)
    {
        hash = Fnv1a.Mix(hash, _entries.Count);
        foreach (var entry in _entries)
        {
            hash = Fnv1a.Mix(hash, entry.Machine.Sequence);
            hash = Fnv1a.Mix(hash, (int)entry.Path);
            hash = Fnv1a.Mix(hash, entry.Level);
        }
        return hash;
    }

    /// <summary>Mixes each tank's held key and refusal, which show nowhere else until a press, for the sync digest.</summary>
    internal ulong MixLatent(ulong hash)
    {
        hash = Fnv1a.Mix(hash, _tanks.Count);
        foreach (var keys in _tanks)
        {
            hash = Fnv1a.Mix(hash, keys.Held);
            hash = Fnv1a.Mix(hash, keys.RefusedTicks);
            hash = Fnv1a.Mix(hash, (int)keys.LastRefusal);
        }
        return hash;
    }

    private MachineUpgradeRefusal TryBuy(Tank tank, int pathIndex)
    {
        Machine? machine = TargetOf(tank);
        MachineUpgradeRefusal refusal = Check(tank, machine, pathIndex, out MachineUpgradeLevel upgrade);
        if (refusal != MachineUpgradeRefusal.None)
        {
            _events.Record(new StepEvent(StepEventKind.MachineUpgradeRefused, machine?.Position ?? tank.Position, tank.Color,
                machine?.Type ?? default, Count: (int)refusal));
            return refusal;
        }

        var target = machine!;
        var path = target.Behavior.UpgradePaths[pathIndex];
        if (Find(target) is not Entry entry)
        {
            entry = new Entry { Machine = target, Path = path };
            _entries.Insert(~IndexOf(target), entry);
        }
        entry.Level = upgrade.Level + 1;
        Apply(target, new MachineUpgradeLevel(entry.Path, entry.Level));
        _events.Record(new StepEvent(StepEventKind.MachineUpgraded, target.Position, tank.Color, target.Type, Count: entry.Level));
        return MachineUpgradeRefusal.None;
    }

    /// <summary>Why buying path <paramref name="pathIndex"/> for <paramref name="machine"/> fails; on success the store has paid.</summary>
    private MachineUpgradeRefusal Check(Tank tank, Machine? machine, int pathIndex, out MachineUpgradeLevel upgrade)
    {
        upgrade = default;
        if (machine == null || tank.Base is not TankBase home)
            return MachineUpgradeRefusal.OutOfReach;
        upgrade = LevelOf(machine);
        var paths = machine.Behavior.UpgradePaths;
        if (upgrade.IsMaxed)
            return MachineUpgradeRefusal.Maxed;
        if (pathIndex < 0 || pathIndex >= paths.Length || !MachineUpgrades.IsAvailable(paths[pathIndex], _rules))
            return MachineUpgradeRefusal.Unavailable;
        if (upgrade.Path is MachineUpgradePath taken && taken != paths[pathIndex])
            return MachineUpgradeRefusal.OtherPath;
        return home.Store.Pay(MachineUpgrades.CostOfLevel(upgrade.Level)) ? MachineUpgradeRefusal.None : MachineUpgradeRefusal.NotEnoughOre;
    }

    /// <summary>Puts a new level into effect where the network holds it: the capacitor's size and idle draw.</summary>
    private void Apply(Machine machine, MachineUpgradeLevel upgrade)
    {
        if (_energy == null || upgrade.Path is not (MachineUpgradePath.Tap or MachineUpgradePath.Conduit))
            return;
        var (capacity, idle) = MachineUpgrades.CapacitorOf(machine.Behavior, upgrade);
        _energy.Resize(machine, capacity, idle, _terrain);
    }

    private int SlotOf(Tank tank)
    {
        int slot = Math.Max(0, tank.Color);
        while (_tanks.Count <= slot)
            _tanks.Add(default);
        return slot;
    }

    private int IndexOf(Machine machine)
    {
        int lo = 0, hi = _entries.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int sequence = _entries[mid].Machine.Sequence;
            if (sequence == machine.Sequence)
                return ReferenceEquals(_entries[mid].Machine, machine) ? mid : ~mid;
            if (sequence < machine.Sequence)
                lo = mid + 1;
            else
                hi = mid - 1;
        }
        return ~lo;
    }

    private Entry? Find(Machine machine) => IndexOf(machine) is int index and >= 0 ? _entries[index] : null;

    /// <summary>One upgraded machine: the path its first purchase fixed and the levels bought on it.</summary>
    private sealed class Entry
    {
        public Machine Machine = null!;
        public MachineUpgradePath Path;
        public int Level;
    }

    /// <summary>A tank's machine upgrade key: the upgrade key held last tick and its last refusal.</summary>
    private struct TankKeys
    {
        public int Held;
        public int RefusedTicks;
        public MachineUpgradeRefusal LastRefusal;
    }
}
