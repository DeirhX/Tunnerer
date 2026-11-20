namespace Emberward.Core.Network;

using Emberward.Core.Config;
using Emberward.Core.Entities.Bases;
using Emberward.Core.Resources;
using Emberward.Core.Thermal;
using Emberward.Core.Types;

/// <summary>Tracks blackouts and shield recovery for bases in an energy match.</summary>
public sealed class BasePowerSystem
{
    private readonly List<State> _states = new();

    private sealed class State
    {
        public TankBase Base = null!;
        public bool Dark;
        public int ShieldTicksRemaining;
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

    public double ShieldWarmupRemaining(TankBase tankBase) => IsDark(tankBase) || tankBase.IsDestroyed
        ? 0.0 : (StateOf(tankBase)?.ShieldTicksRemaining ?? 0) / (double)Tweaks.Perf.TargetFps;

    public double ShieldFraction(TankBase tankBase)
    {
        if (tankBase.IsDestroyed || IsDark(tankBase))
            return 0.0;
        int remaining = StateOf(tankBase)?.ShieldTicksRemaining ?? 0;
        double warmup = 1.0 - (double)remaining / Tweaks.Base.ShieldWarmupTicks;
        return Tweaks.Base.MaxShieldFraction * tankBase.ChargeFill * warmup;
    }

    internal int Damage(TankBase tankBase, int rawDamage)
    {
        double damage = IsDark(tankBase)
            ? rawDamage * Tweaks.Base.DarkDamageMultiplier
            : rawDamage * (1.0 - ShieldFraction(tankBase));
        return Math.Max(1, (int)Math.Ceiling(damage));
    }

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

    internal void Advance()
    {
        foreach (var state in _states)
        {
            state.Dark |= state.Base.Charge <= 0.0;
            if (state.Dark)
            {
                if (state.Base.Charge >= Tweaks.Base.RestartCharge && !state.Base.IsDestroyed)
                {
                    state.Dark = false;
                    state.ShieldTicksRemaining = Tweaks.Base.ShieldWarmupTicks;
                }
            }
            else if (state.ShieldTicksRemaining > 0)
                state.ShieldTicksRemaining--;
        }
    }

    internal ulong MixState(ulong hash)
    {
        foreach (var state in _states)
        {
            hash = Fnv1a.Mix(hash, state.Dark);
            hash = Fnv1a.Mix(hash, state.ShieldTicksRemaining);
        }
        return hash;
    }
}
