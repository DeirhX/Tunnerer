namespace Emberward.Core.Network;

using Emberward.Core.Config;
using Emberward.Core.Entities.Bases;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Terrain;
using Emberward.Core.Types;

/// <summary>
/// The energy network (DESIGN.md §4.9), in a world whose rules run it (<see cref="Match.MatchRules.EnergyNetwork"/>).
/// Every planted machine has a capacitor (<see cref="MachineBehavior.Capacitor"/>). Live links between the side's nodes
/// form islands supplied by their generators and, when reachable, their base. Nearby harvesters can draw from an
/// island in line of sight. Once a tick each island
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
/// (<see cref="StoredCharge"/>, counted by <see cref="World.SumSystemCharge"/>); settling turns it into heat beside the
/// machine, booked as charge turned back into heat, so the ledger balances on every tick.
/// </para>
/// </summary>
public sealed partial class EnergyNetwork
{
    private readonly SimulationSettings _settings;

    public EnergyNetwork(SimulationSettings settings) => _settings = settings;

    private const int TierCount = (int)PowerTier.Economy + 1;

    // One per planted machine, in machine sequence order (Find looks a machine up by halving).
    private readonly List<Capacitor> _capacitors = new();
    private readonly List<PowerIsland> _islands = new();
    // The last tick whose idle draw the islands have paid; settling brings a capacitor up to here.
    private int _paidThrough;
    private int _seenPowerVersion = -1;
    private int _localIslandCount;

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
        var capacitor = new Capacitor
        {
            Machine = machine,
            SettledThrough = _paidThrough,
            IdleDrawPerTick = machine.Behavior.Capacitor.IdleDrawPerTick,
        };
        int at = IndexOf(machine);
        _capacitors.Insert(at >= 0 ? at : ~at, capacitor);
        if (machine.Behavior.IsGenerator)
            _seenPowerVersion = -1;
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
        machine.Charge += capacitor.Pending + capacitor.Reserve;
        capacitor.Reserve = 0.0;
        capacitor.Pending = 0.0;
        _capacitors.RemoveAt(index);
    }

    /// <summary>Brings the machine's capacitor up to date: its level now, and the heat of what it spent in the ground.</summary>
    public void Settle(Machine machine, ITerrainAccess terrain)
    {
        if (Find(machine) is Capacitor capacitor)
            Settle(capacitor, terrain);
    }

    /// <summary>
    /// Connects a harvester to its base's grid when <paramref name="coverage"/> covers it, or to a nearby local grid when
    /// cut off. It takes its charge through the node that covers it (<see cref="TankCoverage.Point"/>), so that node's
    /// throughput limit applies to it.
    /// </summary>
    public void SetCovered(Machine machine, TankCoverage coverage, MachineList machines, ITerrainPixelReadOnly terrain, List<Machine> near)
    {
        if (Find(machine) is not Capacitor capacitor)
            return;
        PowerIsland? island = null;
        Capacitor? via = null;
        if (machine.OwnerBase is TankBase owner)
        {
            if (coverage.Covered)
            {
                island = IslandOf(owner);
                via = CoveringNode(capacitor, coverage, machines, near);
            }
            else if (LocalIslandAt(machine.Position, owner, owner.Upgrades.NodeCoverageRadius, machines, terrain, near) is Capacitor local)
            {
                island = local.Island;
                via = local;
            }
        }
        if (!ReferenceEquals(island, capacitor.Island))
            Move(capacitor, island);
        SetParent(capacitor, via);
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

    /// <summary>Refreshes island membership if the link map made a new power search.</summary>
    public void Refresh(LinkMap links, IReadOnlyList<TankBase> bases)
    {
        if (links.PowerVersion == _seenPowerVersion)
            return;
        _seenPowerVersion = links.PowerVersion;
        foreach (var tankBase in bases)
            IslandOf(tankBase);
        RefreshIslands();
    }

    /// <summary>Settles a share of the capacitors, then pays idle draw and tops up the ones still filling.</summary>
    /// <param name="events">Where a node damaged by overheating is recorded (<see cref="World.StepEvents"/>); null records nothing.</param>
    public void Advance(ITerrainAccess terrain, BasePowerSystem? basePower = null, StepEventLog? events = null)
    {
        SettleShare(terrain, events);
        int tick = _paidThrough + 1;
        foreach (var island in _islands)
            Pay(island, tick, basePower);
        _paidThrough = tick;
    }

    /// <summary>Charge stored in generator reserves and idle charge islands have paid that is not yet heat.</summary>
    public double StoredCharge()
    {
        double total = 0.0;
        foreach (var capacitor in _capacitors)
        {
            total += capacitor.Pending + capacitor.Reserve;
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
        var (production, _) = capacitor.Production.Last(_paidThrough);
        var (_, passed) = capacitor.Passed.Last(_paidThrough);
        return new MachinePower(charge, machine.ChargeCapacity,
            inflow * WindowToSecond + (fed ? idleRate : 0.0),
            outflow * WindowToSecond + (fed || charge > 0.0 ? idleRate : 0.0),
            fed, production * WindowToSecond)
        {
            PassedPerSecond = passed * WindowToSecond,
            LimitPerSecond = machine.Behavior.IsLinkNode ? LimitOf(capacitor) * Tweaks.Perf.TargetFps : 0.0,
            Heat = capacitor.Heat,
        };
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
        var (generated, _) = island?.GeneratorProduction.Last(_paidThrough) ?? default;
        return new BasePower(tankBase.Charge, tankBase.ChargeCapacity, (made + generated) * WindowToSecond, drawn * WindowToSecond,
            generated * WindowToSecond);
    }

    /// <summary>Mixes the network's state, which no other part of the world shows, for the world hash.</summary>
    internal ulong MixState(ulong hash)
    {
        hash = Fnv1a.Mix(hash, _paidThrough);
        hash = Fnv1a.Mix(hash, _seenPowerVersion);
        hash = Fnv1a.Mix(hash, _localIslandCount);
        foreach (var capacitor in _capacitors)
        {
            hash = Fnv1a.Mix(hash, capacitor.Machine.Sequence);
            hash = Fnv1a.Mix(hash, capacitor.SettledThrough);
            hash = Fnv1a.Mix(hash, capacitor.Pending);
            hash = Fnv1a.Mix(hash, capacitor.IsFilling);
            hash = Fnv1a.Mix(hash, capacitor.Island?.Base?.Color ?? -1);
            hash = Fnv1a.Mix(hash, capacitor.Island?.Generator?.Sequence ?? -1);
            hash = Fnv1a.Mix(hash, capacitor.Reserve);
            hash = Fnv1a.Mix(hash, (ulong)capacitor.QuietGeneratorStamp);
            hash = MixThroughput(hash, capacitor);
        }
        foreach (var island in _islands)
        {
            hash = Fnv1a.Mix(hash, island.Base?.Color ?? -1);
            hash = Fnv1a.Mix(hash, island.Generator?.Sequence ?? -1);
            foreach (bool fed in island.TierFed)
                hash = Fnv1a.Mix(hash, fed);
            hash = MixOrder(hash, island.Members);
            hash = MixOrder(hash, island.Producers);
            hash = MixOrder(hash, island.Filling);
            hash = Fnv1a.Mix(hash, island.TreeDirty);
        }
        return hash;
    }

    private static ulong MixOrder(ulong hash, List<Capacitor> capacitors)
    {
        hash = Fnv1a.Mix(hash, capacitors.Count);
        foreach (var capacitor in capacitors)
            hash = Fnv1a.Mix(hash, capacitor.Machine.Sequence);
        return hash;
    }

    private static double WindowToSecond => (double)Tweaks.Perf.TargetFps / Tweaks.Energy.FlowWindowTicks;

    private static double IdleDraw(Capacitor capacitor) => capacitor.IdleDrawPerTick;

    private static int TierOf(Capacitor capacitor) => (int)capacitor.Machine.Behavior.Capacitor.Tier;

    private static bool IsFed(Capacitor capacitor) =>
        capacitor.Island is PowerIsland island && island.TierFed[TierOf(capacitor)];

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
    private void SettleShare(ITerrainAccess terrain, StepEventLog? events)
    {
        int period = Tweaks.Energy.SettlePeriodTicks;
        for (int i = (_paidThrough + 1) % period; i < _capacitors.Count; i += period)
        {
            var capacitor = _capacitors[i];
            if (capacitor.Machine.Behavior.IsLinkNode)
                CheckHeat(capacitor, terrain, events);
            Settle(capacitor, terrain);
        }
    }

    private void Move(Capacitor capacitor, PowerIsland? to)
    {
        Accrue(capacitor);
        StopFilling(capacitor);
        if (capacitor.Island is PowerIsland from)
        {
            from.TreeDirty = true;
            from.Members.Remove(capacitor);
            if (capacitor.Machine.Behavior.IsGenerator)
                from.Producers.Remove(capacitor);
            SumIdle(from, TierOf(capacitor));
        }
        capacitor.Island = to;
        if (to == null)
            return;
        to.Members.Add(capacitor);
        to.TreeDirty = true;
        if (capacitor.Machine.Behavior.IsGenerator)
            to.Producers.Add(capacitor);
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
        capacitor.Allowance = 0.0;
        island.Filling.Add(capacitor);
        island.TreeDirty = true;
    }

    private static void StopFilling(Capacitor capacitor)
    {
        if (!capacitor.IsFilling)
            return;
        capacitor.IsFilling = false;
        if (capacitor.Island is PowerIsland island)
        {
            island.Filling.Remove(capacitor);
            island.TreeDirty = true;
        }
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
    private void Pay(PowerIsland island, int tick, BasePowerSystem? basePower)
    {
        double drawn = 0.0;
        bool useBaseStore = island.Base is { IsDestroyed: false } home && basePower?.IsDark(home) != true;
        double available = island.Available(useBaseStore);
        int producerIndex = 0;
        // Worked out once, before any tier draws, so that what the nodes pass on this tick stays within their limits.
        if (island.TreeDirty)
            RebuildTree(island);
        for (int tier = 0; tier < TierCount; tier++)
        {
            double idle = island.IdleByTier[tier];
            if (!island.TierFed[tier])
            {
                if (available < idle * Tweaks.Energy.RefeedIdleTicks)
                    continue;
                SetTierFed(island, tier, true);
            }
            if (available < idle)
            {
                SetTierFed(island, tier, false);
                continue;
            }
            double tierDraw = island.DrawCharge(idle, useBaseStore, ref producerIndex);
            tierDraw += TopUp(island, tier, tick, useBaseStore, ref producerIndex);
            drawn += tierDraw;
            available = Math.Max(0.0, available - tierDraw);
        }
        island.Draw.Add(tick, drawn, 0.0);
        if (island.Base is TankBase tankBase)
            island.Production.Add(tick, tankBase.EngineOutputPerTick, 0.0);
    }

    /// <summary>
    /// Tops up the filling capacitors of one tier, in the order they started filling, while the store lasts: each by at
    /// most what the throughput tree allows it (<see cref="RebuildTree"/>, worked out at the start of the tick: one that
    /// started filling since takes nothing until the next), and what the nodes on its way lose as heat is drawn with it
    /// (<see cref="PassThrough"/>).
    /// </summary>
    private static double TopUp(PowerIsland island, int tier, int tick, bool useBaseStore, ref int producerIndex)
    {
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
                double want = Math.Min(capacitor.Allowance, room);
                double asked = want * capacitor.LossMultiplier;
                double taken = island.DrawCharge(asked, useBaseStore, ref producerIndex);
                double given = taken >= asked ? want : taken / capacitor.LossMultiplier;
                machine.Charge += given;
                drawn += taken;
                capacitor.Flow.Add(tick, given, 0.0);
                if (taken > given)
                    PassThrough(capacitor, island, given, taken, tick);
                if (room - given <= 0.0)
                {
                    capacitor.IsFilling = false;
                    island.TreeDirty = true;
                    continue;
                }
            }
            filling[kept++] = capacitor;
        }
        filling.RemoveRange(kept, filling.Count - kept);
        return drawn;
    }

    /// <summary>One machine's capacitor bookkeeping (its level is <see cref="Machine.Charge"/>).</summary>
    private sealed partial class Capacitor
    {
        public Machine Machine = null!;
        public PowerIsland? Island;
        // The last tick its level and pending heat account for.
        public int SettledThrough;
        // Charge spent (idle draw) that is not yet heat in the ground.
        public double Pending;
        public bool IsFilling;
        public FlowWindow Flow;
        // A generator's contribution to its island's shared reserve. The charge follows this generator on a split.
        public double Reserve;
        public FlowWindow Production;
        public long QuietGeneratorStamp = -1;
        // Charge it spends per tick standing: its behavior's, or more after an upgrade (Resize).
        public double IdleDrawPerTick;
    }

    /// <summary>A connected group with producers, consumers and a shared charge balance.</summary>
    private sealed partial class PowerIsland
    {
        public TankBase? Base;
        public Machine? Generator;
        public List<Capacitor> Producers = new();
        public List<Capacitor> Members = new();
        public List<Capacitor> Filling = new();
        public double[] IdleByTier = new double[TierCount];
        public bool[] TierFed = [true, true, true];
        public FlowWindow Production;
        public FlowWindow Draw;
        public FlowWindow GeneratorProduction;

        public double Available(bool useBaseStore)
        {
            double total = useBaseStore ? Base!.Charge : 0.0;
            foreach (var producer in Producers)
                total += producer.Reserve;
            return total;
        }

        // A tick never refills a reserve while paying, so the next draw can skip producers that are already empty.
        public double DrawCharge(double wanted, bool useBaseStore, ref int producerIndex)
        {
            if (wanted <= 0.0)
                return 0.0;
            double drawn = 0.0;
            while (producerIndex < Producers.Count && drawn < wanted)
            {
                var producer = Producers[producerIndex];
                double share = Math.Min(wanted - drawn, producer.Reserve);
                producer.Reserve -= share;
                drawn += share;
                if (producer.Reserve <= 0.0)
                    producerIndex++;
            }
            return drawn + (useBaseStore ? Base!.DrawCharge(wanted - drawn) : 0.0);
        }
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
/// <param name="OutPerSecond">Charge a second it spent (idle draw and use), over the last whole second.</param>
/// <param name="IsFed">A member of an island, in a tier the island feeds.</param>
/// <param name="ProductionPerSecond">Charge produced a second, over the last whole second.</param>
public readonly record struct MachinePower(double Charge, double Capacity, double InPerSecond, double OutPerSecond, bool IsFed,
    double ProductionPerSecond = 0.0)
{
    /// <summary>Charge a second it passed on to the machines behind it, over the last whole second.</summary>
    public double PassedPerSecond { get; init; }

    /// <summary>The most it passes on a second now (its Conduit level and heat included); 0 for a machine that is not a node.</summary>
    public double LimitPerSecond { get; init; }

    /// <summary>Whether it runs hot from what it carries: throttled, or overheated and losing health.</summary>
    public ThroughputHeat Heat { get; init; }
}

/// <summary>A base's charge store as an overlay shows it (<see cref="World.BasePowerOf"/>).</summary>
/// <param name="Charge">The store's level.</param>
/// <param name="Capacity">The most it holds.</param>
/// <param name="ProductionPerSecond">Charge a second its engine and linked generators made, over the last whole second.</param>
/// <param name="DrawPerSecond">Charge a second its island drew (idle draw and top-ups), over the last whole second.</param>
/// <param name="GeneratorProductionPerSecond">The part of production that came from linked generators.</param>
public readonly record struct BasePower(double Charge, double Capacity, double ProductionPerSecond, double DrawPerSecond,
    double GeneratorProductionPerSecond = 0.0, bool IsDark = false, double ShieldFraction = 0.0,
    double ShieldWarmupSeconds = 0.0);
