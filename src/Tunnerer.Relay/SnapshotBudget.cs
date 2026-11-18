namespace Tunnerer.Relay;

/// <summary>Shared by room uploads and socket send queues. A slow receiver still pays for queued chunks after a hold ends.</summary>
internal sealed class SnapshotBudget
{
    private const int Limit = 256 * 1024 * 1024;
    private int _used;

    public bool TryReserve(int bytes)
    {
        if (Interlocked.Add(ref _used, bytes) <= Limit)
            return true;
        Release(bytes);
        return false;
    }

    public void Release(int bytes) => Interlocked.Add(ref _used, -bytes);
}
