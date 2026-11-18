namespace Tunnerer.Protocol;

/// <summary>
/// The chunks of one snapshot as they arrive, checked: all for the same tick, in order, and no more bytes than were
/// announced. The relay keeps an upload in one to pass it on; a joiner keeps one to put the bytes back together.
/// </summary>
public sealed class SnapshotAssembly
{
    private readonly List<SnapshotChunk> _chunks = [];

    /// <param name="maxBytes">The most bytes the snapshot may have; more is refused.</param>
    public SnapshotAssembly(int tick, int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        Tick = tick;
        MaxBytes = maxBytes;
    }

    public int Tick { get; }

    public int MaxBytes { get; }

    /// <summary>The bytes received so far.</summary>
    public int Length { get; private set; }

    /// <summary>The chunks received so far, in order.</summary>
    public IReadOnlyList<SnapshotChunk> Chunks => _chunks;

    /// <summary>
    /// Takes the next chunk. False, with nothing taken, for a chunk of another tick, out of order, or past
    /// <see cref="MaxBytes"/>.
    /// </summary>
    public bool Add(SnapshotChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        // Split fills each chunk except the last. Enforcing that shape also bounds the number of retained chunks.
        if (chunk.Tick != Tick || chunk.Index != _chunks.Count || chunk.Bytes.IsDefaultOrEmpty ||
            chunk.Bytes.Length > SnapshotChunk.MaxBytes || chunk.Bytes.Length > MaxBytes - Length ||
            Length % SnapshotChunk.MaxBytes != 0)
            return false;
        _chunks.Add(chunk);
        Length += chunk.Bytes.Length;
        return true;
    }

    /// <summary>The bytes received, in one array.</summary>
    public byte[] ToArray()
    {
        byte[] bytes = new byte[Length];
        int at = 0;
        foreach (SnapshotChunk chunk in _chunks)
        {
            chunk.Bytes.CopyTo(bytes, at);
            at += chunk.Bytes.Length;
        }
        return bytes;
    }
}
