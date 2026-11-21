namespace Emberward.Core.Entities.Tanks;

using Emberward.Core.Config;
using Emberward.Core.Match;
using Emberward.Core.Snapshots;
using Emberward.Core.Types;

/// <summary>
/// The tank's shield (DESIGN.md §4.7), in a world whose rules run it (<see cref="MatchRules.TankShield"/>): a
/// regenerating layer in front of the hull. Damage takes the shield first and the overflow goes to the hull
/// (<see cref="Absorb"/>); the shield waits <see cref="Tweaks.TankShield.RegenDelayTicks"/> after a hit and then
/// refills (<see cref="Advance"/>). The hull never regenerates here: only a base repairs it.
/// </summary>
/// <remarks>
/// The system keeps each tank's state itself, by its colour, so <see cref="Tank"/> has no shield fields. A state of
/// nothing missing is a full shield, so a tank that was never hit has no entry to grow. Cost follows change: a full
/// shield is one comparison per tick.
/// </remarks>
public sealed class TankShieldSystem
{
    // Each tank's shield, by its colour (its spawn index); grown as tanks first take a hit.
    private readonly List<TankState> _tanks = new();

    /// <summary>
    /// Takes <paramref name="amount"/> off <paramref name="tank"/>: the shield first, then the hull for what is left.
    /// Any damage, even all of it absorbed, restarts the regeneration delay.
    /// </summary>
    internal DamageTaken Absorb(Tank tank, int amount)
    {
        if (tank.Color < 0)
            return new DamageTaken(0, amount);
        int slot = SlotOf(tank);
        TankState state = _tanks[slot];
        int shield = Tweaks.TankShield.Capacity - state.Missing;
        int toShield = Math.Min(amount, shield);
        state.Missing += toShield;
        state.DelayTicks = Tweaks.TankShield.RegenDelayTicks;
        _tanks[slot] = state;
        return new DamageTaken(toShield, amount - toShield, ShieldBroken: toShield > 0 && state.Missing >= Tweaks.TankShield.Capacity);
    }

    /// <summary>
    /// How big the fire on <paramref name="tank"/> is, 0 to 1: nothing while the hull is above
    /// <see cref="Tweaks.TankShield.FireIgniteHullFraction"/>, growing to full size at an empty hull. A pure function
    /// of the hull, so it needs no state of its own; a dead tank does not burn.
    /// </summary>
    public static float FireLevelOf(Tank tank)
    {
        if (tank.IsDead)
            return 0f;
        float ignite = Tweaks.TankShield.FireIgniteHullFraction;
        return Math.Clamp((ignite - tank.HullFraction) / ignite, 0f, 1f);
    }

    /// <summary>
    /// One tick of <paramref name="tank"/>'s regeneration, from <see cref="Tank.Advance"/> while it lives. The delay
    /// after a hit counts down every tick; the shield refills (in the tick the delay ends, and after it) only while
    /// <paramref name="canRefill"/>: on its network and not in a dark base.
    /// </summary>
    internal void Advance(Tank tank, bool canRefill)
    {
        if (tank.Color < 0 || tank.Color >= _tanks.Count)
            return;
        TankState state = _tanks[tank.Color];
        if (state.Missing == 0 && state.DelayTicks == 0)
            return;
        if (state.DelayTicks > 0)
            state.DelayTicks--;
        state.Refilling = false;
        if (state.DelayTicks == 0 && canRefill)
        {
            state.Missing = Math.Max(0, state.Missing - Tweaks.TankShield.RegenPerTick);
            state.Refilling = true;
        }
        _tanks[tank.Color] = state;
    }

    /// <summary>Gives <paramref name="tank"/> a full shield again (it died or respawned).</summary>
    internal void Clear(Tank tank)
    {
        if (tank.Color >= 0 && tank.Color < _tanks.Count)
            _tanks[tank.Color] = default;
    }

    /// <summary>What the HUD and the renderer read of <paramref name="tank"/>'s shield.</summary>
    public TankShieldView ViewOf(Tank tank)
    {
        TankState state = tank.Color >= 0 && tank.Color < _tanks.Count ? _tanks[tank.Color] : default;
        int shield = Tweaks.TankShield.Capacity - state.Missing;
        return new TankShieldView(true, shield, Tweaks.TankShield.Capacity,
            shield / (float)Tweaks.TankShield.Capacity, tank.HullFraction,
            Regenerating: state.Missing > 0 && state.Refilling, state.DelayTicks, FireLevelOf(tank));
    }

    /// <summary>Mixes every tank's shield, for <see cref="WorldStateHash.Compute"/>.</summary>
    internal ulong MixState(ulong hash)
    {
        hash = Fnv1a.Mix(hash, _tanks.Count);
        foreach (var state in _tanks)
        {
            hash = Fnv1a.Mix(hash, state.Missing);
            hash = Fnv1a.Mix(hash, state.DelayTicks);
            hash = Fnv1a.Mix(hash, state.Refilling);
        }
        return hash;
    }

    private int SlotOf(Tank tank)
    {
        int slot = Math.Max(0, tank.Color);
        while (_tanks.Count <= slot)
            _tanks.Add(default);
        return slot;
    }

    /// <summary>A tank's shield: how much of it is gone, and the ticks left before it starts to refill.</summary>
    private struct TankState
    {
        public int Missing;
        public int DelayTicks;

        // Whether the last tick refilled it (what the HUD means by regenerating: not just the delay being over).
        public bool Refilling;
    }
}

/// <summary>
/// What one hit took off a tank: <see cref="Shield"/> points from its shield and <see cref="Hull"/> from its hull.
/// <see cref="ShieldBroken"/> when this hit emptied the shield.
/// </summary>
public readonly record struct DamageTaken(int Shield, int Hull, bool ShieldBroken = false)
{
    /// <summary>Whether the hit took anything at all.</summary>
    public bool Any => Shield > 0 || Hull > 0;
}

/// <summary>
/// A tank's shield and hull as the HUD and the renderer read them. <see cref="Active"/> is false (and the rest zero)
/// when the match rules run no shield.
/// </summary>
public readonly record struct TankShieldView(bool Active, int Shield, int ShieldCapacity, float ShieldFill, float HullFill,
    bool Regenerating, int RegenDelayTicks, float FireLevel = 0f);
