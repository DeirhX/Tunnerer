namespace Tunnerer.Core.Regrowth;

using Tunnerer.Core.Snapshots;
using Tunnerer.Core.Types;

/// <summary>A circle in which no new dirt grows: the range of a planted relay or pylon.</summary>
public readonly record struct RegrowthBlocker(Position Center, int Radius);

/// <summary>
/// The blockers in force, filed by the part of the map they reach so that asking whether a cell is blocked looks at
/// the few blockers near it and not at every machine on the map. It is refiled only when the list changes.
/// </summary>
internal sealed class RegrowthBlockerIndex
{
    private const int BucketSize = 64;

    private readonly List<RegrowthBlocker> _listed = new();
    [NotSaved("scratch: filled and read within Update")]
    private readonly HashSet<RegrowthBlocker> _lookup = new();
    private int _bucketsX;
    private int _bucketsY;
    // Bucket b holds _inBucket[_start[b] .. _start[b + 1]).
    private int[] _start = [];
    private int[] _fill = [];
    private RegrowthBlocker[] _inBucket = [];

    /// <summary>
    /// Takes the blockers now in force. Returns false when they are the ones already filed; otherwise refiles them
    /// and lists in <paramref name="lifted"/> the ones that were in force and no longer are.
    /// </summary>
    public bool Update(IReadOnlyList<RegrowthBlocker> blockers, int width, int height, List<RegrowthBlocker> lifted)
    {
        lifted.Clear();
        if (SameAsListed(blockers))
            return false;

        _lookup.Clear();
        for (int i = 0; i < blockers.Count; i++)
            _lookup.Add(blockers[i]);
        foreach (RegrowthBlocker old in _listed)
            if (!_lookup.Contains(old))
                lifted.Add(old);
        _listed.Clear();
        _listed.AddRange(blockers);

        _bucketsX = (width + BucketSize - 1) / BucketSize;
        _bucketsY = (height + BucketSize - 1) / BucketSize;
        int buckets = _bucketsX * _bucketsY;
        if (_start.Length != buckets + 1)
        {
            _start = new int[buckets + 1];
            _fill = new int[buckets];
        }

        // Count per bucket, turn the counts into where each bucket starts, then fill.
        Array.Clear(_start);
        foreach (RegrowthBlocker blocker in _listed)
        {
            var (minX, minY, maxX, maxY) = BucketsOf(blocker);
            for (int by = minY; by <= maxY; by++)
                for (int bx = minX; bx <= maxX; bx++)
                    _start[bx + by * _bucketsX + 1]++;
        }
        for (int b = 0; b < buckets; b++)
            _start[b + 1] += _start[b];
        if (_inBucket.Length < _start[buckets] || _inBucket.Length > 4 * Math.Max(16, _start[buckets]))
            _inBucket = new RegrowthBlocker[_start[buckets]];
        Array.Copy(_start, _fill, buckets);
        foreach (RegrowthBlocker blocker in _listed)
        {
            var (minX, minY, maxX, maxY) = BucketsOf(blocker);
            for (int by = minY; by <= maxY; by++)
                for (int bx = minX; bx <= maxX; bx++)
                    _inBucket[_fill[bx + by * _bucketsX]++] = blocker;
        }
        return true;
    }

    public bool IsBlocked(int x, int y)
    {
        if (_listed.Count == 0)
            return false;
        int bucket = x / BucketSize + y / BucketSize * _bucketsX;
        for (int i = _start[bucket]; i < _start[bucket + 1]; i++)
        {
            RegrowthBlocker blocker = _inBucket[i];
            int dx = x - blocker.Center.X, dy = y - blocker.Center.Y;
            if (dx * dx + dy * dy <= blocker.Radius * blocker.Radius)
                return true;
        }
        return false;
    }

    private bool SameAsListed(IReadOnlyList<RegrowthBlocker> blockers)
    {
        if (blockers.Count != _listed.Count)
            return false;
        for (int i = 0; i < blockers.Count; i++)
            if (blockers[i] != _listed[i])
                return false;
        return true;
    }

    private (int MinX, int MinY, int MaxX, int MaxY) BucketsOf(RegrowthBlocker blocker) => (
        Math.Max(0, (blocker.Center.X - blocker.Radius) / BucketSize),
        Math.Max(0, (blocker.Center.Y - blocker.Radius) / BucketSize),
        Math.Min(_bucketsX - 1, (blocker.Center.X + blocker.Radius) / BucketSize),
        Math.Min(_bucketsY - 1, (blocker.Center.Y + blocker.Radius) / BucketSize));
}
