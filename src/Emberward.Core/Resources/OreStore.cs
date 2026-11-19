namespace Emberward.Core.Resources;

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
