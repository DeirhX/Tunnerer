namespace Emberward.Core.Entities.Machines;

using Emberward.Core.Snapshots;
using Emberward.Core.Config;
using Emberward.Core.Types;
using Emberward.Core.Terrain;
using Emberward.Core.Rendering;
using Emberward.Core.Network;

/// <summary>
/// Every machine in the world, in the order they were added. The machines are also filed by where they stand
/// (<see cref="CollectNear"/>), so that asking what is near a place looks at the machines there and not at the whole
/// list; a machine reports its own moves.
/// </summary>
public class MachineList : ISnapshotLoaded
{
    // Side of the squares machines are filed in, as a power of two.
    private const int BucketShift = 5;

    private static readonly Comparison<Machine> BySequence = static (a, b) => a.Sequence.CompareTo(b.Sequence);

    private readonly List<Machine> _machines = new();
    private readonly List<Machine> _chargers = new();
    // Looked up by key only, never enumerated, so its order cannot reach the simulation.
    [NotSaved("rebuilt on load from the machine list (OnSnapshotLoaded); looked up by key only")]
    private readonly Dictionary<long, List<Machine>> _buckets = new();
    private int _nextSequence;

    public IReadOnlyList<Machine> Machines => _machines;
    public int Count => _machines.Count;

    /// <summary>The machines that charge batteries (<see cref="MachineBehavior.IsCharger"/>), in list order.</summary>
    public IReadOnlyList<Machine> Chargers => _chargers;

    public Machine Add(Machine machine)
    {
        machine.Sequence = _nextSequence++;
        machine.List = this;
        _machines.Add(machine);
        if (machine.Behavior.IsCharger)
            _chargers.Add(machine);
        BucketAt(machine.Position, create: true)!.Add(machine);
        return machine;
    }

    /// <summary>
    /// Fills <paramref name="into"/> with the machines standing within <paramref name="reach"/> cells of
    /// <paramref name="center"/> along both axes (a square), whatever their state. With <paramref name="inListOrder"/>
    /// they come in the order of <see cref="Machines"/>, which is what a rule that says "the first machine that..."
    /// needs; without it the order is arbitrary but the same on every run.
    /// </summary>
    public void CollectNear(Position center, int reach, List<Machine> into, bool inListOrder = true)
    {
        into.Clear();
        int minX = (center.X - reach) >> BucketShift, maxX = (center.X + reach) >> BucketShift;
        int minY = (center.Y - reach) >> BucketShift, maxY = (center.Y + reach) >> BucketShift;
        for (int by = minY; by <= maxY; by++)
            for (int bx = minX; bx <= maxX; bx++)
            {
                if (!_buckets.TryGetValue(BucketKey(bx, by), out var bucket))
                    continue;
                foreach (Machine machine in bucket)
                    if (Math.Abs(machine.Position.X - center.X) <= reach && Math.Abs(machine.Position.Y - center.Y) <= reach)
                        into.Add(machine);
            }
        if (inListOrder && into.Count > 1)
            into.Sort(BySequence);
    }

    internal void NoteMoved(Machine machine, Position from)
    {
        var old = BucketAt(from, create: false);
        var now = BucketAt(machine.Position, create: true)!;
        if (ReferenceEquals(old, now))
            return;
        old?.Remove(machine);
        now.Add(machine);
    }

    private void Remove(int index)
    {
        Machine machine = _machines[index];
        _machines.RemoveAt(index);
        if (machine.Behavior.IsCharger)
            _chargers.Remove(machine);
        BucketAt(machine.Position, create: false)?.Remove(machine);
        machine.List = null;
    }

    private List<Machine>? BucketAt(Position position, bool create)
    {
        long key = BucketKey(position.X >> BucketShift, position.Y >> BucketShift);
        if (!_buckets.TryGetValue(key, out var bucket) && create)
            _buckets[key] = bucket = new List<Machine>();
        return bucket;
    }

    void ISnapshotLoaded.OnSnapshotLoaded()
    {
        _buckets.Clear();
        foreach (Machine machine in _machines)
            BucketAt(machine.Position, create: true)!.Add(machine);
    }

    private static long BucketKey(int bucketX, int bucketY) => ((long)bucketY << 32) | (uint)bucketX;

    public void Advance(in MachineContext ctx, TimeSpan dt)
    {
        for (int i = _machines.Count - 1; i >= 0; i--)
        {
            var machine = _machines[i];
            if (!machine.IsAlive)
            {
                ctx.Energy?.Remove(machine, ctx.Terrain);
                ctx.Upgrades?.Remove(machine);
                Wreck(machine, ctx.Terrain);
                Remove(i);
                continue;
            }
            if (machine.OwnerBase?.IsDestroyed != true)
                machine.Advance(ctx, dt);
        }
    }

    /// <summary>
    /// A destroyed machine leaves the network (the link map rebuilds on its next advance), dumps its battery into the
    /// ground as heat and scorches the ground it stood on, like an explosion's decal, for regrowth to fade.
    /// </summary>
    private static void Wreck(Machine machine, ITerrainAccess terrain)
    {
        if (machine.LinkPoint != null)
            machine.LinkPoint.IsEnabled = false; // the link map prunes disabled points
        if (machine.Charge > 0.0)
            machine.DischargeAsHeat(terrain, machine.Charge);
        if (machine.State == MachineState.Planted)
            Scorch(machine.Position, terrain);
    }

    private static void Scorch(Position center, ITerrainAccess terrain)
    {
        int half = Tweaks.Machine.WreckScorchHalfSize;
        for (int dy = -half; dy <= half; dy++)
            for (int dx = -half; dx <= half; dx++)
            {
                var pos = new Position(center.X + dx, center.Y + dy);
                if (!terrain.IsInside(pos)) continue;
                var pix = terrain.GetPixelRaw(pos);
                // Open ground and dirt burn; rock and base cells keep their material.
                if (Pixel.IsOpenGround(pix) || Pixel.IsDiggable(pix))
                    terrain.SetPixel(pos, TerrainPixel.Decal); // metered: a dirt cell's heat change is booked
            }
    }

    /// <summary>Copies each machine's render state; a generator's fraction is measured from <paramref name="generatorMaxChargePerTick"/>.</summary>
    public int CopyRenderStates(Span<MachineRenderState> destination, double generatorMaxChargePerTick)
    {
        int count = Math.Min(destination.Length, _machines.Count);
        for (int i = 0; i < count; i++)
        {
            var m = _machines[i];
            destination[i] = new MachineRenderState(
                Position: m.Position,
                Type: m.Type,
                State: m.State,
                IsAlive: m.IsAlive,
                IsPowered: m.LinkPoint?.IsPowered ?? false,
                IsPumping: m.LastPumpedEnergy > NetworkStatus.PumpingThreshold,
                PumpFraction: (float)Math.Clamp(m.LastPumpedEnergy / Tweaks.Machine.RelayHeatPumpPerTick, 0.0, 1.0),
                IsPumpLimited: m.LastPumpLimitedByBase,
                IsStalled: m.IsStalled,
                Heading: m.Heading,
                SenseHeat: m.SenseHeat,
                Charge: m.ChargeFraction,
                IsReturning: m.IsReturning,
                IsCharging: m.ChargingFrom is { IsAlive: true },
                ChargingFrom: m.ChargingFrom?.Position ?? default,
                IsGivingCharge: m.LastChargeGiven > 0.0,
                Anchor: m.Anchor,
                GenerationFraction: m.Behavior.IsGenerator ? (float)(m.LastChargeMade / generatorMaxChargePerTick) : 0f);
        }
        return count;
    }
}
