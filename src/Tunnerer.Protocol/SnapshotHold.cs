namespace Tunnerer.Protocol;

/// <summary>
/// One snapshot for all joiners waiting at a held tick. One seated player supplies the chunks. When a second
/// player is available it independently saves the same tick and reports just the two hashes.
/// </summary>
public sealed class SnapshotHold
{
    private readonly SnapshotAssembly _upload;
    private readonly int _uploader;
    private readonly int? _verifier;
    private SnapshotDone? _uploaded;
    private SnapshotDone? _verified;

    public SnapshotHold(int tick, int uploader, int? verifier, int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(uploader);
        if (verifier < 0 || verifier == uploader)
            throw new ArgumentException("The verifier must occupy a separate player seat.", nameof(verifier));
        _upload = new SnapshotAssembly(tick, maxBytes);
        _uploader = uploader;
        _verifier = verifier;
    }

    public int Tick => _upload.Tick;

    public bool AddChunk(int slot, SnapshotChunk chunk) =>
        slot == _uploader && _uploaded == null && _upload.Add(chunk);

    /// <summary>Accepts one report per requested seat. The caller also submits its hashes to the room for desync reporting.</summary>
    public bool Finish(int slot, SnapshotDone done)
    {
        ArgumentNullException.ThrowIfNull(done);
        if (done.Tick != Tick)
            return false;
        if (slot == _uploader && _uploaded == null && done.Length > 0 && done.Length == _upload.Length)
            _uploaded = done;
        else if (slot == _verifier && _verified == null && done.Length == 0)
            _verified = done;
        else
            return false;
        return true;
    }

    /// <summary>Available only after the upload and all required checks agree.</summary>
    public SnapshotOffer? Offer =>
        _uploaded is { } done && (_verifier == null ||
            (_verified?.Digest == done.Digest && _verified.SyncHash == done.SyncHash))
            ? new SnapshotOffer(Tick, done.Length, done.Digest, done.SyncHash)
            : null;

    public IReadOnlyList<SnapshotChunk> Chunks => _upload.Chunks;
}
