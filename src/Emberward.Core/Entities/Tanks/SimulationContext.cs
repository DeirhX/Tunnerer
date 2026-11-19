namespace Emberward.Core.Entities.Tanks;

using Emberward.Core.Config;
using Emberward.Core.Entities.Projectiles;
using Emberward.Core.Terrain;
using Emberward.Core.Types;
using Emberward.Core.Network;
using Emberward.Core.Entities.Bases;

/// <summary>
/// Narrow view of the world passed to <see cref="Tank.Advance"/>,
/// replacing the previous full <see cref="World"/> dependency.
/// Exposes only what Tank actually needs, making it unit-testable with stubs.
/// </summary>
public readonly record struct SimulationContext
{
    public required ITerrainPixelAccessor Terrain { get; init; }
    public required ITerrainHeatSource Heat { get; init; }
    public required ProjectileList Projectiles { get; init; }
    public required Func<Position, TankBase?> CheckBaseCollision { get; init; }
    public required SimulationSettings Settings { get; init; }

    /// <summary>Machine list a tank builds relays into. Null in contexts that do not support building.</summary>
    public Machines.MachineList? Machines { get; init; }

    /// <summary>Plants a carried machine where it stands (it joins the link network). Null when building is unsupported.</summary>
    public Action<Machines.Machine, TankBase?>? PlantMachine { get; init; }

    /// <summary>
    /// Optional: whether a relay planted at a position would link to a powered network. When set, a tank refuses to
    /// plant a relay that would not link, so the dirt it cost is not wasted.
    /// </summary>
    public Func<Types.Position, bool>? CanPlantRelayAt { get; init; }

    /// <summary>
    /// Optional: the network coverage of a tank's own base at a position (DESIGN.md §4.6). When set, a tank outside
    /// it heats up and a tank inside it is cooled down the links; when null the rule is off.
    /// </summary>
    public Func<Tank, TankCoverage>? CoverageFor { get; init; }

    /// <summary>
    /// Optional: whether a live tank or a planted machine stands on a cell. When set, a dead tank's ore does not land
    /// under one (<see cref="Resources.OreDrop"/>).
    /// </summary>
    public Func<Position, bool>? IsOccupied { get; init; }

    /// <summary>
    /// Optional: whether another tank or another side's machine stops a tank from standing at a position facing a
    /// sprite direction (<see cref="Collision.CollisionSolver.BlocksTank"/>). When null, tanks collide with terrain only.
    /// </summary>
    public Func<Tank, Position, int, bool>? IsTankBlocked { get; init; }

    /// <summary>Where the tank records what it did this step (<see cref="World.StepEvents"/>). Null records nothing.</summary>
    public StepEventLog? Events { get; init; }
}
