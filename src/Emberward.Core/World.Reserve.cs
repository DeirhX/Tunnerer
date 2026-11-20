namespace Emberward.Core;

using Emberward.Core.Entities.Tanks;

public partial class World
{
    /// <summary>
    /// <paramref name="tank"/>'s reserve as the HUD and the bot read it (DESIGN.md §4.9): its stage, the stage's counter
    /// and whether the recall key burns. Inactive (the default) when the rules run no reserve or the tank is on its
    /// network. Output only.
    /// </summary>
    public TankReserveView ReserveOf(Tank tank) => _reserve?.ViewOf(tank) ?? default;

    /// <summary>
    /// Whether the recall key gives up and respawns <paramref name="tank"/> now: its off-network clock has run out
    /// (<see cref="Tank.CanRecall"/>) and, under the reserve rule, it has nothing left to burn.
    /// </summary>
    public bool RecallOffered(Tank tank) => tank.CanRecall && !ReserveOf(tank).CanBurn;

    /// <summary>The tank's reserve, for the hashes; null when the match rules leave it out.</summary>
    internal TankReserveSystem? ReserveSystem => _reserve;
}
