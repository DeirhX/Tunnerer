namespace Tunnerer.Core.Entities;

using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Projectiles;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

/// <summary>
/// Narrow view of the world passed to <see cref="Tank.Advance"/>,
/// replacing the previous full <see cref="World"/> dependency.
/// Exposes only what Tank actually needs, making it unit-testable with stubs.
/// </summary>
public readonly record struct SimulationContext
{
    public required ITerrainPixelReader Terrain { get; init; }
    public required ITerrainHeatSource Heat { get; init; }
    public required ProjectileList Projectiles { get; init; }
    public required Func<Position, TankBase?> CheckBaseCollision { get; init; }
    public required SimulationSettings Settings { get; init; }
}
