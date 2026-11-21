namespace Emberward.Core.Network;

using Emberward.Core.Config;
using Emberward.Core.Entities.Bases;
using Emberward.Core.Resources;
using Emberward.Core.Terrain;
using Emberward.Core.Thermal;
using Emberward.Core.Types;

/// <summary>What became of a base's gate shield when a shell hit a gate (<see cref="BasePowerSystem.AbsorbGateHit"/>).</summary>
public enum GateHit
{
    /// <summary>The gates were open to the enemy: the shell flies on.</summary>
    Open,
    /// <summary>The shield took the shell and holds.</summary>
    Absorbed,
    /// <summary>The shield took the shell and is spent: the gates are now open to the enemy.</summary>
    Broken,
}

/// <summary>
/// Tracks blackouts and the gate shield for bases in an energy match (DESIGN.md §4.9). The shield is a pool of
/// points: enemy shells that hit a gate drain it, the base's charge store refills it, and while it holds the gates
/// are shut to enemy tanks and shells. Spent, the gates stand open to the enemy until it is back to
/// <see cref="Tweaks.Keep.ShieldRestoreFill"/>.
/// </summary>
public sealed class BasePowerSystem
{
    private readonly List<State> _states = new();

    private sealed class State
    {
        public TankBase Base = null!;
        public bool Dark;
        public double Shield = Tweaks.Keep.ShieldCapacity;
        public bool GatesOpen;
    }

    private State? StateOf(TankBase tankBase)
    {
        int color = tankBase.Color;
        return (uint)color < (uint)_states.Count && ReferenceEquals(_states[color].Base, tankBase)
            ? _states[color] : null;
    }

    internal void Register(TankBase tankBase) => _states.Add(new State { Base = tankBase });

    /// <summary>True from an empty store until its charge reaches the restart threshold.</summary>
    public bool IsDark(TankBase tankBase) => tankBase.Charge <= 0.0 || StateOf(tankBase) is { Dark: true };

    /// <summary>How full the gate shield is, 0 to 1; nothing for a destroyed base.</summary>
    public double ShieldFill(TankBase tankBase) => tankBase.IsDestroyed
        ? 0.0 : Math.Clamp((StateOf(tankBase)?.Shield ?? 0.0) / Tweaks.Keep.ShieldCapacity, 0.0, 1.0);

    /// <summary>
    /// Whether the gates are shut to the enemy: the shield has not been spent since it last came back, and the base
    /// stands. The owner's tank and shells pass either way.
    /// </summary>
    public bool GatesShut(TankBase tankBase) =>
        !tankBase.IsDestroyed && StateOf(tankBase) is { GatesOpen: false, Shield: > 0.0 };

    /// <summary>
    /// An enemy shell of <paramref name="damage"/> hits a gate: the shield takes it while the gates are shut. Spent,
    /// the gates open, and are drawn open in <paramref name="terrain"/>.
    /// </summary>
    internal GateHit AbsorbGateHit(TankBase tankBase, int damage, ITerrainPixelAccessor terrain)
    {
        if (!GatesShut(tankBase) || StateOf(tankBase) is not { } state)
            return GateHit.Open;
        state.Shield = Math.Max(0.0, state.Shield - damage);
        if (state.Shield > 0.0)
            return GateHit.Absorbed;
        Open(state, terrain);
        return GateHit.Broken;
    }

    private static void Open(State state, ITerrainPixelAccessor terrain)
    {
        state.GatesOpen = true;
        state.Base.PaintGates(terrain, shut: false);
    }

    /// <summary>What a shell of <paramref name="rawDamage"/> takes off the core: more while the base is dark.</summary>
    internal int CoreDamage(TankBase tankBase, int rawDamage) => IsDark(tankBase)
        ? Math.Max(1, (int)Math.Ceiling(rawDamage * Tweaks.Base.DarkDamageMultiplier))
        : rawDamage;

    internal TimeSpan RespawnDelay(TankBase tankBase)
    {
        double fill = IsDark(tankBase) ? 0.0 : tankBase.ChargeFill;
        double extra = Tweaks.Tank.RespawnDarkDelay.Ticks - Tweaks.Tank.RespawnDelay.Ticks;
        return TimeSpan.FromTicks(Tweaks.Tank.RespawnDelay.Ticks + (long)Math.Round(extra * (1.0 - fill)));
    }

    internal int BurnHold(TankBase tankBase, OreStore hold, ThermalLedger ledger)
    {
        if (tankBase.IsDestroyed || !IsDark(tankBase))
            return 0;
        // A blackout can be observed before the end-of-tick latch, including on the first docking tick.
        if (StateOf(tankBase) is { } state)
            state.Dark = true;
        double needed = Tweaks.Base.RestartCharge - tankBase.Charge;
        int units = Math.Min(hold.Ore, Math.Max(0, (int)Math.Ceiling(needed / Tweaks.Base.ChargePerRestartOre)));
        if (units == 0 || !hold.Pay(units))
            return 0;
        double charge = tankBase.StoreCharge(units * Tweaks.Base.ChargePerRestartOre);
        ledger.RecordFromRestartOre(charge);
        return units;
    }

    internal void LatchBlackouts()
    {
        foreach (var state in _states)
            state.Dark |= state.Base.Charge <= 0.0;
    }

    /// <summary>
    /// End of a tick: latches and lifts blackouts, and recharges each shield from its base's store. A gate that
    /// shuts again is redrawn shut and recorded in <paramref name="events"/>; a fallen base's gates stand open.
    /// </summary>
    internal void Advance(ITerrainAccess terrain, StepEventLog? events = null)
    {
        foreach (var state in _states)
        {
            state.Dark |= state.Base.Charge <= 0.0;
            if (state.Base.IsDestroyed)
            {
                if (!state.GatesOpen)
                    Open(state, terrain);
                continue;
            }
            if (state.Dark && state.Base.Charge >= Tweaks.Base.RestartCharge)
                state.Dark = false;
            if (state.Dark)
                continue;

            RechargeShield(state, terrain.Ledger);
            if (state.GatesOpen && state.Shield >= Tweaks.Keep.ShieldRestoreFill * Tweaks.Keep.ShieldCapacity)
            {
                state.GatesOpen = false;
                state.Base.PaintGates(terrain, shut: true);
                events?.Record(new StepEvent(StepEventKind.BaseShieldRestored, state.Base.Position, state.Base.Color));
            }
        }
    }

    /// <summary>
    /// Buys shield with charge from the store, down to the reserve the store keeps. The charge spent ends as heat in
    /// the reactor, so a full reactor holds the recharge back until the radiator has vented.
    /// </summary>
    private static void RechargeShield(State state, ThermalLedger ledger)
    {
        var tankBase = state.Base;
        double missing = Tweaks.Keep.ShieldCapacity - state.Shield;
        if (missing <= 0.0)
            return;
        double wanted = Math.Min(missing, Tweaks.Keep.ShieldRechargePerTick) * Tweaks.Keep.ChargePerShieldPoint;
        double spare = tankBase.Charge - Tweaks.Keep.ShieldReserveCharge;
        double spend = Math.Min(Math.Min(wanted, spare), tankBase.ReactorRoomEnergy);
        if (spend <= 0.0)
            return;
        double heat = tankBase.AcceptHeat(tankBase.DrawCharge(spend));
        ledger.RecordFromCharge(heat);
        state.Shield += heat / Tweaks.Keep.ChargePerShieldPoint;
    }

    internal ulong MixState(ulong hash)
    {
        foreach (var state in _states)
        {
            hash = Fnv1a.Mix(hash, state.Dark);
            hash = Fnv1a.Mix(hash, state.Shield);
            hash = Fnv1a.Mix(hash, state.GatesOpen);
        }
        return hash;
    }
}
