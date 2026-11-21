namespace Emberward.Core.Rendering;

using Emberward.Core.Entities;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Entities.Projectiles;
using Emberward.Core.Types;
using Emberward.Core.Network;

public readonly record struct TankRenderState(
    Position Position,
    int Color,
    int Direction,
    bool IsDead,
    DirectionF TurretDirection,
    int HitFlashTicks = 0,
    bool IsProtected = false);

public readonly record struct ProjectileRenderState(
    PositionF Position,
    VectorF Velocity,
    ProjectileType Type,
    bool IsAlive);

/// <param name="Charge">Capacitor fill, 0..1.</param>
/// <param name="HasCapacitor">It has a capacitor: every planted machine on the energy network, none without it.</param>
public readonly record struct MachineRenderState(
    Position Position,
    MachineType Type,
    MachineState State,
    bool IsAlive,
    bool IsPowered,
    bool IsPumping = false,
    float PumpFraction = 0f,
    bool IsPumpLimited = false,
    bool IsStalled = false,
    Offset Heading = default,
    float SenseHeat = 0f,
    float Charge = 0f,
    bool HasCapacitor = false,
    float GenerationFraction = 0f);

/// <param name="HeatFlow">Heat carried in the last tick, in full relay pump rates (2 = two relays' worth).</param>
/// <param name="OreFlow">Ore unloaded into the network on its way home: 1 just after a unit passed, fading to 0.</param>
public readonly record struct LinkRenderState(
    Position From,
    Position To,
    LinkType Type,
    bool IsAlive,
    float HeatFlow = 0f,
    bool FlowsFromTo = true,
    float OreFlow = 0f,
    bool OreFlowsFromTo = true);

/// <summary>A creature out of its nest, as it is drawn.</summary>
/// <param name="Id">Stable for the creature's life; drives its wriggle.</param>
/// <param name="IsCharged">It still carries its heat: drawn bright, and dull once spent.</param>
/// <param name="IsBurrowing">It is in soil: drawn as a dim glow under the ground.</param>
public readonly record struct MobRenderState(int Id, PositionF Position, VectorF Velocity, bool IsCharged, bool IsBurrowing);

/// <summary>A nest, as it is drawn: <paramref name="Occupied"/> creatures are in it, resting.</summary>
public readonly record struct NestRenderState(Position Centre, int Radius, int Occupied, bool IsAwake, bool IsCleared);
