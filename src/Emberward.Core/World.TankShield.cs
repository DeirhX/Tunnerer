namespace Emberward.Core;

using Emberward.Core.Entities.Tanks;

public partial class World
{
    /// <summary>
    /// <paramref name="tank"/>'s shield and hull as the HUD and the renderer read them (DESIGN.md §4.7). Inactive (the
    /// default) when the rules run no tank shield. Output only.
    /// </summary>
    public TankShieldView TankShieldOf(Tank tank) => _tankShield?.ViewOf(tank) ?? default;

    /// <summary>The tanks' shields, for the hashes; null when the match rules leave it out.</summary>
    internal TankShieldSystem? TankShieldSystem => _tankShield;
}
