namespace Emberward.Core.Entities.Mobs;

using Emberward.Core.Config;
using Emberward.Core.Types;

/// <summary>Wear of one flow-sized block of ground: how much creatures have burrowed through it, as of <see cref="StampTick"/>.</summary>
public struct TrailBlock
{
    /// <summary>The wear as it was at <see cref="StampTick"/>; it fades from there (<see cref="MobTrails.EffectiveWorn"/>).</summary>
    public float Worn { get; set; }

    /// <summary>The swarm's tick when the block was last worn.</summary>
    public int StampTick { get; set; }
}

/// <summary>
/// The trails creatures leave in soil: a wear value for each block of <see cref="Tweaks.Mobs.FieldBlockCells"/> squared
/// cells, kept apart from the terrain (it writes no pixel, so it wakes no heat tile, regrowth, vision or link). A block
/// worn to <see cref="Tweaks.Mobs.TrailLaneThreshold"/> is a lane: creatures run through it as through open ground, and
/// the flow field counts it as open. Wear fades by the clock on read, never by a sweep, so the trails cost only the
/// creatures that are out. Tanks do not use them. Present only with the swarm.
/// </summary>
public sealed class MobTrails
{
    private const int Block = Tweaks.Mobs.FieldBlockCells;

    private readonly int _blocksX;
    private readonly int _blocksY;
    private readonly TrailBlock[] _blocks;

    // Sum of a hash of each worn block, kept up to date as blocks are worn, so the state hash costs nothing per block.
    private ulong _checksum;

    public MobTrails(Size terrainSize)
    {
        _blocksX = (terrainSize.X + Block - 1) / Block;
        _blocksY = (terrainSize.Y + Block - 1) / Block;
        _blocks = new TrailBlock[_blocksX * _blocksY];
    }

    /// <summary>The index of the block holding <paramref name="cell"/>, or -1 off the map.</summary>
    public int BlockOf(Position cell)
    {
        if (cell.X < 0 || cell.Y < 0)
            return -1;
        return BlockIndex(cell.X / Block, cell.Y / Block);
    }

    /// <summary>The index of block (<paramref name="bx"/>, <paramref name="by"/>), or -1 off the map.</summary>
    public int BlockIndex(int bx, int by) =>
        bx < 0 || by < 0 || bx >= _blocksX || by >= _blocksY ? -1 : bx + by * _blocksX;

    /// <summary>The wear of a block at tick <paramref name="now"/>: what was stored, faded linearly to nothing over <see cref="Tweaks.Mobs.TrailLifetimeTicks"/>.</summary>
    public float EffectiveWorn(int block, int now)
    {
        TrailBlock entry = _blocks[block];
        if (entry.Worn <= 0f)
            return 0f;
        float left = 1f - (now - entry.StampTick) / (float)Tweaks.Mobs.TrailLifetimeTicks;
        return left > 0f ? entry.Worn * left : 0f;
    }

    /// <summary>Whether a block is a lane at tick <paramref name="now"/>; -1 (off the map) is not.</summary>
    public bool IsLane(int block, int now) => block >= 0 && EffectiveWorn(block, now) >= Tweaks.Mobs.TrailLaneThreshold;

    /// <summary>Whether block (<paramref name="bx"/>, <paramref name="by"/>) is a lane at tick <paramref name="now"/>.</summary>
    public bool IsLane(int bx, int by, int now) => IsLane(BlockIndex(bx, by), now);

    /// <summary>A creature crossed into a block: fades what is stored to <paramref name="now"/>, adds one crossing, caps it.</summary>
    public void Wear(int block, int now)
    {
        float worn = Math.Min(EffectiveWorn(block, now) + Tweaks.Mobs.TrailWearPerCrossing, Tweaks.Mobs.TrailWearMax);
        _checksum -= HashOf(block, _blocks[block]);
        _blocks[block] = new TrailBlock { Worn = worn, StampTick = now };
        _checksum += HashOf(block, _blocks[block]);
    }

    /// <summary>Mixes the trails into <paramref name="hash"/>.</summary>
    internal ulong Mix(ulong hash) => Fnv1a.Mix(hash, _checksum);

    private static ulong HashOf(int block, TrailBlock entry) =>
        entry.Worn <= 0f ? 0UL : Fnv1a.Mix(Fnv1a.Mix(Fnv1a.Mix(Fnv1a.Offset, block), entry.Worn), entry.StampTick);
}
