namespace Emberward.Protocol;

/// <summary>
/// One client's connection to the relay, as the game sees it. Never blocks: sends are queued and
/// <see cref="TryReceive"/> returns only what has already arrived.
/// </summary>
public interface ILockstepLink
{
    /// <summary>Sends the local player's encoded input for one step.</summary>
    void SendInput(uint input);

    /// <summary>Reports the hash of the local world after <paramref name="tick"/> ticks, for the relay to compare.</summary>
    void SendHash(int tick, ulong hash);

    /// <summary>Reports that, by the local simulation, the match was decided at <paramref name="tick"/>.</summary>
    void SendMatchOver(int tick);

    /// <summary>The next bundle from the relay, in tick order, if one has arrived.</summary>
    bool TryReceive([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TickBundle? bundle);

    /// <summary>
    /// Sends a message of joining a match in progress (docs/late-join-design.md §4): an uploader's snapshot, or a
    /// joiner's <see cref="ReadyToLoad"/>, <see cref="TakeSeat"/> or <see cref="JoinFailed"/>.
    /// </summary>
    void SendJoin(Message message);

    /// <summary>
    /// The next message of joining a match in progress from the relay, if one has arrived: a
    /// <see cref="SnapshotRequest"/>, a <see cref="SnapshotOffer"/> and its chunks, <see cref="Seated"/> or
    /// <see cref="SeatRefused"/>.
    /// </summary>
    bool TryReceiveJoin([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Message? message);
}
