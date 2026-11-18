namespace Tunnerer.Protocol;

/// <summary>
/// The relay's side of one join hold (docs/late-join-design.md §5, §9.2): the match is held after
/// <see cref="Tick"/> ticks, some seated players are asked to save the world there and upload it, and when they all
/// have, the first upload is offered to every joiner waiting. The relay never reads a snapshot. It passes each
/// uploader's digest and sync hash to <see cref="LockstepRoom"/>, which finds two that differ, as it does hashes.
/// </summary>
/// <remarks>
/// Who the joiners are, the time limit and the transfer budget belong to the relay that keeps the hold; this is only
/// what the uploads are and whether they are all in.
/// </remarks>
public sealed class SnapshotHold
{
    /// <summary>The most uploaders asked for one snapshot: two, so that the relay can compare them.</summary>
    public const int MaxUploaders = 2;

    private readonly List<int> _uploaders;
    private readonly Dictionary<int, SnapshotAssembly> _uploads = [];
    private readonly Dictionary<int, SnapshotDone> _done = [];
    private int? _firstDone;

    /// <param name="tick">The ticks closed when the hold began: the snapshot is of the world after them.</param>
    /// <param name="uploaders">The seated slots asked to upload, at most <see cref="MaxUploaders"/>.</param>
    /// <param name="maxBytes">The most compressed bytes one upload may have.</param>
    public SnapshotHold(int tick, IEnumerable<int> uploaders, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(uploaders);
        Tick = tick;
        _uploaders = [.. uploaders.Distinct()];
        if (_uploaders.Count is 0 or > MaxUploaders)
            throw new ArgumentException($"A snapshot is asked of 1 to {MaxUploaders} players.", nameof(uploaders));
        foreach (int slot in _uploaders)
            _uploads[slot] = new SnapshotAssembly(tick, maxBytes);
    }

    /// <summary>The ticks closed when the hold began. Nothing moves until it ends.</summary>
    public int Tick { get; }

    /// <summary>The slots asked to upload.</summary>
    public IReadOnlyList<int> Uploaders => _uploaders;

    /// <summary>
    /// Takes a chunk from an uploader. False for one from a slot not asked, after its upload was done, or that does
    /// not fit (<see cref="SnapshotAssembly.Add"/>).
    /// </summary>
    public bool AddChunk(int slot, SnapshotChunk chunk) =>
        !_done.ContainsKey(slot) && _uploads.TryGetValue(slot, out var upload) && upload.Add(chunk);

    /// <summary>
    /// An uploader says its upload is complete. False for a slot not asked, a second time, another tick, or a length
    /// that is not what arrived.
    /// </summary>
    public bool Finish(int slot, SnapshotDone done)
    {
        ArgumentNullException.ThrowIfNull(done);
        if (_done.ContainsKey(slot) || !_uploads.TryGetValue(slot, out var upload) ||
            done.Tick != Tick || done.Length != upload.Length)
            return false;
        _done[slot] = done;
        _firstDone ??= slot;
        return true;
    }

    /// <summary>True once every uploader asked has finished.</summary>
    public bool IsComplete => _done.Count == _uploaders.Count;

    /// <summary>What a joiner is told before the chunks, from the first finished upload; null until one is.</summary>
    public SnapshotOffer? Offer =>
        _firstDone is int slot && _done[slot] is var done
            ? new SnapshotOffer(done.Tick, done.Length, done.Digest, done.SyncHash)
            : null;

    /// <summary>The chunks a joiner is sent after the offer: the first finished upload's.</summary>
    public IReadOnlyList<SnapshotChunk> Chunks => _firstDone is int slot ? _uploads[slot].Chunks : [];
}
