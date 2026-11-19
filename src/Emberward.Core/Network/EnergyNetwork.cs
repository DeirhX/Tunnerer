namespace Emberward.Core.Network;

using Emberward.Core.Config;
using Emberward.Core.Entities.Bases;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

/// <summary>
/// The energy network (DESIGN.md §4.9), in a world whose rules run it (<see cref="Match.MatchRules.EnergyNetwork"/>).
/// Every planted machine has a capacitor (<see cref="MachineBehavior.Capacitor"/>); the base's charge store is the
/// base's. A machine whose base can reach it is a member of that base's island: a link node (relay, pylon) when its
/// power comes home to its own base over live links, a harvester while that network covers it. Once a tick each island
/// pays, tier by tier (<see cref="PowerTier"/>), the idle draw of its members and then tops up the capacitors that are
/// not full, each by at most its transfer rate. A tier the store cannot pay is starved: its members drain as if cut off,
/// and the lowest tier goes first. A machine cut off keeps its charge and drains it by its idle draw and its use.
/// <para>
/// Cost follows what changes. A full member's idle draw is paid from a per-tier sum and a cut-off machine's level falls
/// on a straight line, so neither is looked at per tick: a capacitor is brought up to date (settled) only when it is
/// used, changes island or tier, or, once in <c>Energy.SettlePeriodTicks</c>, to put the heat of its idle draw into the
/// ground. Per tick the network looks at the islands, the capacitors still filling and that share of the rest; islands
/// change membership only when the link map's power changes (<see cref="LinkMap.PowerVersion"/>) or a harvester's
/// coverage does.
/// </para>
/// <para>
/// Energy: idle charge an island paid that is not yet heat stays stored charge until it is settled
/// (<see cref="PendingCharge"/>, counted by <see cref="World.SumSystemCharge"/>); settling turns it into heat beside the
/// machine, booked as charge turned back into heat, so the ledger balances on every tick.
/// </para>
/// </summary>
public sealed class EnergyNetwork
{
    private const int TierCount = (int)PowerTier.Economy + 1;

    // One per planted machine, in machine sequence order (Find looks a machine up by halving).
    private readonly List<Capacitor> _capacitors = new();
    private readonly List<PowerIsland> _islands = new();
    // The last tick whose idle draw the islands have paid; settling brings a capacitor up to here.
    private int _paidThrough;
    private int _seenPowerVersion = -1;

    /// <summary>Capacitors registered (planted machines not yet wrecked).</summary>
    public int CapacitorCount => _capacitors.Count;

    /// <summary>Capacitors the islands topped up during the last tick: what the network cost beyond its islands.</summary>
    public int FillingCount
    {
        get
        {
            int count = 0;
            foreach (var island in _islands)
                count += island.Filling.Count;
            return count;
        }
    }

    /// <summary>A machine was planted: it gets its capacitor, cut off until its island takes it.</summary>
    public void Register(Machine machine)
    {
        var capacitor = new Capacitor { Machine = machine, SettledThrough = _paidThrough };
        int at = IndexOf(machine);
        _capacitors.Insert(at >= 0 ? at : ~at, capacitor);
    }

    /// <summary>
    /// A machine is wrecked: its capacitor is settled and leaves the network, and any charge it could not yet turn into
    /// heat goes back into the machine's own charge, which the wreck dumps.
    /// </summary>
    public void Remove(Machine machine, ITerrainAccess terrain)
    {
        int index = IndexOf(machine);
        if (index < 0)
            return;
        var capacitor = _capacitors[index];
        Settle(capacitor, terrain);
        Move(capacitor, null);
        machine.Charge += capacitor.Pending;
        capacitor.Pending = 0.0;
        _capacitors.RemoveAt(index);
    }

    /// <summary>Brings the machine's capacitor up to date: its level now, and the heat of what it spent in the ground.</summary>
    public void Settle(Machine machine, ITerrainAccess terrain)
    {
        if (Find(machine) is Capacitor capacitor)
            Settle(capacitor, terrain);
    }

    /// <summary>A harvester's coverage by its own base's linked network: covered, it is a member of its base's island.</summary>
    public void SetCovered(Machine machine, bool covered)
    {
        if (Find(machine) is not Capacitor capacitor)
            return;
        PowerIsland? island = covered && machine.OwnerBase is TankBase owner ? IslandOf(owner) : null;
        if (!ReferenceEquals(island, capacitor.Island))
            Move(capacitor, island);
    }

    /// <summary>
    /// Spends up to <paramref name="amount"/> of the machine's charge as heat in the ground beside it (what it has, at
    /// most). A member of a fed island is then topped up again.
    /// </summary>
    public void Spend(Machine machine, double amount, ITerrainAccess terrain)
    {
        if (Find(machine) is not Capacitor capacitor)
            return;
        Settle(capacitor, terrain);
        amount = Math.Min(amount, machine.Charge);
        if (amount <= 0.0)
            return;
        double before = machine.Charge;
        machine.DischargeAsHeat(terrain, amount);
        capacitor.Flow.Add(_paidThrough + 1, 0.0, before - machine.Charge);
        if (IsFed(capacitor))
            StartFilling(capacitor);
    }

    /// <summary>One tick: islands brought up to date if the link network's power changed, a share settled, then each island pays.</summary>
    public void Advance(ITerrainAccess terrain, LinkMap links, IReadOnlyList<TankBase> bases)
    {
        foreach (var tankBase in bases)
            IslandOf(tankBase);
        if (links.PowerVersion != _seenPowerVersion)
        {
            _seenPowerVersion = links.PowerVersion;
            foreach (var capacitor in _capacitors)
            {
                if (!capacitor.Machine.Behavior.IsLinkNode)
                    continue;
                PowerIsland? island = LinkedIsland(capacitor.Machine);
                if (!ReferenceEquals(island, capacitor.Island))
                    Move(capacitor, island);
            }
        }
        SettleShare(terrain);

        int tick = _paidThrough + 1;
        foreach (var island in _islands)
            Pay(island, tick);
        _paidThrough = tick;
    }

    /// <summary>Idle charge islands have paid that is not yet heat: stored charge, for the energy balance.</summary>
    public double PendingCharge()
    {
        double total = 0.0;
        foreach (var capacitor in _capacitors)
        {
            total += capacitor.Pending;
            if (IsFed(capacitor))
                total += IdleDraw(capacitor) * (_paidThrough - capacitor.SettledThrough);
        }
        return total;
    }

    /// <summary>
    /// What an overlay shows for a machine: its charge now, its capacity, and its inflow and outflow in charge a second,
    /// summed over the last whole second. Null for a machine without a capacitor here. Output only.
    /// </summary>
    public MachinePower? PowerOf(Machine machine)
    {
        if (Find(machine) is not Capacitor capacitor)
            return null;
        bool fed = IsFed(capacitor);
        double idle = IdleDraw(capacitor);
        double charge = fed
            ? machine.Charge
            : Math.Max(0.0, machine.Charge - idle * (_paidThrough - capacitor.SettledThrough));
        var (inflow, outflow) = capacitor.Flow.Last(_paidThrough);
        double idleRate = idle * Tweaks.Perf.TargetFps;
        return new MachinePower(charge, machine.ChargeCapacity,
            inflow * WindowToSecond + (fed ? idleRate : 0.0),
            outflow * WindowToSecond + (fed || charge > 0.0 ? idleRate : 0.0),
            fed);
    }

    /// <summary>
    /// What an overlay shows for a base: its store, the charge its engine made and the charge its island drew, both a
    /// second, summed over the last whole second. Output only.
    /// </summary>
    public BasePower PowerOf(TankBase tankBase)
    {
        PowerIsland? island = FindIsland(tankBase);
        var (made, _) = island?.Production.Last(_paidThrough) ?? default;
        var (drawn, _) = island?.Draw.Last(_paidThrough) ?? default;
        return new BasePower(tankBase.Charge, tankBase.ChargeCapacity, made * WindowToSecond, drawn * WindowToSecond);
    }

    /// <summary>Mixes the network's state, which no other part of the world shows, for the world hash.</summary>
    internal ulong MixState(ulong hash)
    {
        hash = Fnv1a.Mix(hash, _paidThrough);
        hash = Fnv1a.Mix(hash, _seenPowerVersion);
        foreach (var capacitor in _capacitors)
        {
            hash = Fnv1a.Mix(hash, capacitor.Machine.Sequence);
            hash = Fnv1a.Mix(hash, capacitor.SettledThrough);
            hash = Fnv1a.Mix(hash, capacitor.Pending);
            hash = Fnv1a.Mix(hash, capacitor.IsFilling);
            hash = Fnv1a.Mix(hash, capacitor.Island?.Base.Color ?? -1);
        }
        foreach (var island in _islands)
            foreach (bool fed in island.TierFed)
                hash = Fnv1a.Mix(hash, fed);
        return hash;
    }

    private static double WindowToSecond => (double)Tweaks.Perf.TargetFps / Tweaks.Energy.FlowWindowTicks;

    private static double IdleDraw(Capacitor capacitor) => capacitor.Machine.Behavior.Capacitor.IdleDrawPerTick;

    private static int TierOf(Capacitor capacitor) => (int)capacitor.Machine.Behavior.Capacitor.Tier;

    private static bool IsFed(Capacitor capacitor) =>
        capacitor.Island is PowerIsland island && island.TierFed[TierOf(capacitor)];

    /// <summary>The island of a link node's own base when its power comes home to that base, else null.</summary>
    private PowerIsland? LinkedIsland(Machine machine)
    {
        if (machine.OwnerBase is not TankBase owner || owner.LinkPoint is null ||
            machine.LinkPoint is not { IsPowered: true } point)
            return null;
        // Power can reach a node through another base's network; it feeds only from its own.
        for (int hops = 0; point.UpstreamLink is Link link && hops < 64; hops++)
            point = ReferenceEquals(link.From, point) ? link.To : link.From;
        return ReferenceEquals(point, owner.LinkPoint) ? IslandOf(owner) : null;
    }

    private PowerIsland? FindIsland(TankBase tankBase)
    {
        foreach (var island in _islands)
            if (ReferenceEquals(island.Base, tankBase))
                return island;
        return null;
    }

    private PowerIsland IslandOf(TankBase tankBase)
    {
        if (FindIsland(tankBase) is PowerIsland found)
            return found;
        var island = new PowerIsland { Base = tankBase };
        _islands.Add(island);
        return island;
    }

    private int IndexOf(Machine machine)
    {
        int lo = 0, hi = _capacitors.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int sequence = _capacitors[mid].Machine.Sequence;
            if (sequence == machine.Sequence)
                return ReferenceEquals(_capacitors[mid].Machine, machine) ? mid : ~mid;
            if (sequence < machine.Sequence)
                lo = mid + 1;
            else
                hi = mid - 1;
        }
        return ~lo;
    }

    private Capacitor? Find(Machine machine) => IndexOf(machine) is int index and >= 0 ? _capacitors[index] : null;

    /// <summary>
    /// Brings the capacitor's level up to the last paid tick: a fed one has had its idle draw paid by the island (now
    /// pending heat), a cut-off one has drained its own charge into pending heat.
    /// </summary>
    private void Accrue(Capacitor capacitor)
    {
        int ticks = _paidThrough - capacitor.SettledThrough;
        if (ticks <= 0)
            return;
        double idle = IdleDraw(capacitor) * ticks;
        if (IsFed(capacitor))
        {
            capacitor.Pending += idle;
        }
        else
        {
            var machine = capacitor.Machine;
            double drained = Math.Min(machine.Charge, idle);
            machine.Charge -= drained;
            capacitor.Pending += drained;
        }
        capacitor.SettledThrough = _paidThrough;
    }

    /// <summary><see cref="Accrue"/>, then the pending charge becomes heat in the ground beside the machine.</summary>
    private void Settle(Capacitor capacitor, ITerrainAccess terrain)
    {
        Accrue(capacitor);
        if (capacitor.Pending <= 0.0)
            return;
        double heat = terrain.AddEnergyInRadiusArea(capacitor.Machine.Position, Tweaks.Energy.IdleHeatRadius,
            capacitor.Pending, TerrainGrid.ThermalField.Terrain);
        capacitor.Pending -= heat;
        terrain.Ledger.RecordFromCharge(heat);
    }

    /// <summary>
    /// Settles the capacitors whose turn it is: every <c>Energy.SettlePeriodTicks</c>-th one, starting from this tick's
    /// place in the period, so each is settled once a period and a tick looks at a share of them (none at all on most
    /// ticks of a small network). A capacitor added or removed may shift another's turn once, which only moves when its
    /// idle heat reaches the ground.
    /// </summary>
    private void SettleShare(ITerrainAccess terrain)
    {
        int period = Tweaks.Energy.SettlePeriodTicks;
        for (int i = (_paidThrough + 1) % period; i < _capacitors.Count; i += period)
            Settle(_capacitors[i], terrain);
    }

    private void Move(Capacitor capacitor, PowerIsland? to)
    {
        Accrue(capacitor);
        StopFilling(capacitor);
        if (capacitor.Island is PowerIsland from)
        {
            from.Members.Remove(capacitor);
            SumIdle(from, TierOf(capacitor));
        }
        capacitor.Island = to;
        if (to == null)
            return;
        to.Members.Add(capacitor);
        SumIdle(to, TierOf(capacitor));
        if (IsFed(capacitor))
            StartFilling(capacitor);
    }

    // Summed again from the members rather than kept by adding and taking away, so it never drifts.
    private static void SumIdle(PowerIsland island, int tier)
    {
        double sum = 0.0;
        foreach (var member in island.Members)
            if (TierOf(member) == tier)
                sum += IdleDraw(member);
        island.IdleByTier[tier] = sum;
    }

    private static void StartFilling(Capacitor capacitor)
    {
        var machine = capacitor.Machine;
        if (capacitor.IsFilling || capacitor.Island is not PowerIsland island || machine.Charge >= machine.ChargeCapacity)
            return;
        capacitor.IsFilling = true;
        island.Filling.Add(capacitor);
    }

    private static void StopFilling(Capacitor capacitor)
    {
        if (!capacitor.IsFilling)
            return;
        capacitor.IsFilling = false;
        capacitor.Island?.Filling.Remove(capacitor);
    }

    /// <summary>Feeds or starves a tier of the island: its members are settled up to now under the old rule first.</summary>
    private void SetTierFed(PowerIsland island, int tier, bool fed)
    {
        foreach (var member in island.Members)
            if (TierOf(member) == tier)
                Accrue(member);
        island.TierFed[tier] = fed;
        foreach (var member in island.Members)
        {
            if (TierOf(member) != tier)
                continue;
            if (fed)
                StartFilling(member);
            else
                StopFilling(member);
        }
    }

    /// <summary>The island pays for tick <paramref name="tick"/>: tier by tier, its members' idle draw, then top-ups.</summary>
    private void Pay(PowerIsland island, int tick)
    {
        var store = island.Base;
        double drawn = 0.0;
        for (int tier = 0; tier < TierCount; tier++)
        {
            double idle = island.IdleByTier[tier];
            if (!island.TierFed[tier])
            {
                if (store.Charge < idle * Tweaks.Energy.RefeedIdleTicks)
                    continue;
                SetTierFed(island, tier, true);
            }
            if (store.Charge < idle)
            {
                SetTierFed(island, tier, false);
                continue;
            }
            drawn += store.DrawCharge(idle);
            drawn += TopUp(island, tier, tick);
        }
        island.Draw.Add(tick, drawn, 0.0);
        island.Production.Add(tick, store.EngineOutputPerTick, 0.0);
    }

    /// <summary>Tops up the filling capacitors of one tier, in the order they started filling, while the store lasts.</summary>
    private static double TopUp(PowerIsland island, int tier, int tick)
    {
        var store = island.Base;
        var filling = island.Filling;
        double drawn = 0.0;
        int kept = 0;
        for (int i = 0; i < filling.Count; i++)
        {
            var capacitor = filling[i];
            if (TierOf(capacitor) == tier)
            {
                var machine = capacitor.Machine;
                double room = machine.ChargeCapacity - machine.Charge;
                double given = store.DrawCharge(Math.Min(machine.Behavior.Capacitor.TransferPerTick, room));
                machine.Charge += given;
                drawn += given;
                capacitor.Flow.Add(tick, given, 0.0);
                if (room - given <= 0.0)
                {
                    capacitor.IsFilling = false;
                    continue;
                }
            }
            filling[kept++] = capacitor;
        }
        filling.RemoveRange(kept, filling.Count - kept);
        return drawn;
    }

    /// <summary>One machine's capacitor bookkeeping (its level is <see cref="Machine.Charge"/>).</summary>
    private sealed class Capacitor
    {
        public Machine Machine = null!;
        public PowerIsland? Island;
        // The last tick its level and pending heat account for.
        public int SettledThrough;
        // Charge spent (idle draw) that is not yet heat in the ground.
        public double Pending;
        public bool IsFilling;
        public FlowWindow Flow;
    }

    /// <summary>The machines one base feeds, its idle draw per tier, which tiers it feeds and what is filling.</summary>
    private sealed class PowerIsland
    {
        public TankBase Base = null!;
        public List<Capacitor> Members = new();
        public List<Capacitor> Filling = new();
        public double[] IdleByTier = new double[TierCount];
        public bool[] TierFed = [true, true, true];
        public FlowWindow Production;
        public FlowWindow Draw;
    }

    /// <summary>
    /// Charge in and out summed over windows of <c>Energy.FlowWindowTicks</c>: the window in progress and the last
    /// whole one. Touched only when charge moves, so a quiet capacitor costs nothing.
    /// </summary>
    private struct FlowWindow
    {
        public int Window;
        public double In;
        public double Out;
        public double LastIn;
        public double LastOut;

        public void Add(int tick, double inflow, double outflow)
        {
            int window = tick / Tweaks.Energy.FlowWindowTicks;
            if (window != Window)
            {
                bool next = window == Window + 1;
                LastIn = next ? In : 0.0;
                LastOut = next ? Out : 0.0;
                In = Out = 0.0;
                Window = window;
            }
            In += inflow;
            Out += outflow;
        }

        /// <summary>What moved in the last whole window before <paramref name="tick"/>'s.</summary>
        public readonly (double In, double Out) Last(int tick)
        {
            int window = tick / Tweaks.Energy.FlowWindowTicks;
            return window == Window ? (LastIn, LastOut)
                : window == Window + 1 ? (In, Out)
                : (0.0, 0.0);
        }
    }
}

/// <summary>A machine's capacitor as an overlay shows it (<see cref="World.MachinePowerOf"/>).</summary>
/// <param name="Charge">Its level now.</param>
/// <param name="Capacity">The most it holds.</param>
/// <param name="InPerSecond">Charge a second it took from its island, over the last whole second.</param>
/// <param name="OutPerSecond">Charge a second it spent (idle draw and use) or passed on, over the last whole second.</param>
/// <param name="IsFed">A member of its base's island, in a tier the island feeds.</param>
public readonly record struct MachinePower(double Charge, double Capacity, double InPerSecond, double OutPerSecond, bool IsFed);

/// <summary>A base's charge store as an overlay shows it (<see cref="World.BasePowerOf"/>).</summary>
/// <param name="Charge">The store's level.</param>
/// <param name="Capacity">The most it holds.</param>
/// <param name="ProductionPerSecond">Charge a second its engine made, over the last whole second.</param>
/// <param name="DrawPerSecond">Charge a second its island drew (idle draw and top-ups), over the last whole second.</param>
public readonly record struct BasePower(double Charge, double Capacity, double ProductionPerSecond, double DrawPerSecond);
