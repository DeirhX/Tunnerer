namespace Tunnerer.Core.Resources;

public readonly record struct Heat(int Value)
{
    public static implicit operator int(Heat h) => h.Value;
    public static Heat operator +(Heat a, Heat b) => new(a.Value + b.Value);
    public static Heat operator -(Heat a, Heat b) => new(a.Value - b.Value);
}

public readonly record struct Health(int Value)
{
    public static implicit operator int(Health h) => h.Value;
    public static Health operator +(Health a, Health b) => new(a.Value + b.Value);
    public static Health operator -(Health a, Health b) => new(a.Value - b.Value);
}

public struct ReactorState
{
    public Heat Heat;
    public Health Health;

    public ReactorState(Heat heat, Health health) { Heat = heat; Health = health; }
    public ReactorState(int heat, int health) { Heat = new(heat); Health = new(health); }

    public static ReactorState operator +(ReactorState a, ReactorState b) => new(a.Heat + b.Heat, a.Health + b.Health);
    public static ReactorState operator -(ReactorState a, ReactorState b) => new(a.Heat - b.Heat, a.Health - b.Health);
    public bool IsNegative => Heat < 0 || Health < 0;
    public void TrimNegative() { Heat = new(Math.Max(0, Heat)); Health = new(Math.Max(0, Health)); }
}

public class Reactor
{
    public ReactorState Current;
    public ReactorState Capacity;

    public Reactor(ReactorState initial, ReactorState capacity)
    {
        Current = initial;
        Capacity = capacity;
    }

    public Heat Heat => Current.Heat;
    public Health Health => Current.Health;
    public Heat HeatCapacity => Capacity.Heat;
    public Health HealthCapacity => Capacity.Health;

    public bool CanPay(ReactorState cost) => !(Current - cost).IsNegative;

    public bool Pay(ReactorState cost)
    {
        if (!CanPay(cost)) return false;
        Current -= cost;
        return true;
    }

    public void Add(ReactorState gift)
    {
        Current += gift;
        var excess = Current - Capacity;
        excess.TrimNegative();
        Current -= excess;
    }

    public bool Exhaust(ReactorState reduction)
    {
        Current -= reduction;
        bool negative = Current.IsNegative;
        Current.TrimNegative();
        return !negative;
    }

    public void Absorb(Reactor other)
    {
        Current += other.Current;
        other.Current = default;
        var excess = Current - Capacity;
        excess.TrimNegative();
        Current -= excess;
        other.Current += excess;
    }
}

/// <summary>
/// A store of emberite ore (DESIGN.md §6.3), the only currency: a tank's hold or a base's store. In units: a plain ore
/// cell is one, a core cell three. Ore is only ever moved between stores, paid for something or spilled into the
/// ground, so every unit is accounted for.
/// </summary>
public sealed class OreStore
{
    private int _ore;

    public OreStore(int initial, int capacity)
    {
        Capacity = capacity;
        _ore = Math.Clamp(initial, 0, capacity);
    }

    /// <summary>Units in the store, between 0 and <see cref="Capacity"/>.</summary>
    public int Ore
    {
        get => _ore;
        set => _ore = Math.Clamp(value, 0, Capacity);
    }

    /// <summary>
    /// The most the store holds. It may grow (a hold upgrade); it never shrinks below what the store holds.
    /// </summary>
    public int Capacity { get; private set; }

    /// <summary>Room left before the store is full.</summary>
    public int Room => Math.Max(0, Capacity - _ore);

    public bool IsFull => Room == 0;

    public bool CanPay(int cost) => _ore >= cost;

    /// <summary>Takes <paramref name="cost"/> out of the store; false (and nothing taken) when it holds less.</summary>
    public bool Pay(int cost)
    {
        if (!CanPay(cost)) return false;
        _ore -= cost;
        return true;
    }

    /// <summary>Puts up to <paramref name="units"/> into the store, as many as fit, and returns how many did.</summary>
    public int Add(int units)
    {
        int added = Math.Clamp(units, 0, Room);
        _ore += added;
        return added;
    }

    /// <summary>Moves up to <paramref name="max"/> units into <paramref name="to"/>, as many as it has room for; returns how many moved.</summary>
    public int MoveTo(OreStore to, int max)
    {
        int moved = Math.Min(Math.Min(max, _ore), to.Room);
        if (moved <= 0)
            return 0;
        _ore -= moved;
        to._ore += moved;
        return moved;
    }

    internal void SetCapacity(int capacity) => Capacity = Math.Max(capacity, _ore);
}
