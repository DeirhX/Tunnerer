namespace Emberward.Protocol;

/// <summary>Rules about a room that the relay and the client must agree on, so they are written once.</summary>
public static class RoomRules
{
    /// <summary>
    /// The host is whoever sits in the lowest occupied slot; -1 in an empty room. Only the host's start request
    /// counts, and the client shows the host's name.
    /// </summary>
    public static int HostSlot(int slots, Func<int, bool> isOccupied)
    {
        for (int slot = 0; slot < slots; slot++)
            if (isOccupied(slot))
                return slot;
        return -1;
    }
}
