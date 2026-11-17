namespace Tunnerer.Core.Rendering;

using Tunnerer.Core.Entities;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Entities.Projectiles;
using Tunnerer.Core.Types;
using Tunnerer.Core.Network;

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

/// <param name="Charge">Battery fill, 0..1 (always 0 for machines without a battery).</param>
/// <param name="IsCharging">True while a pylon at <paramref name="ChargingFrom"/> is filling this battery.</param>
/// <param name="IsGivingCharge">True for a pylon that handed out charge at its last action.</param>
/// <param name="Anchor">Where the machine was planted. Unlike <paramref name="Position"/>, it does not change while a harvester crawls.</param>
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
    bool IsReturning = false,
    bool IsCharging = false,
    Position ChargingFrom = default,
    bool IsGivingCharge = false,
    Position Anchor = default);

/// <param name="HeatFlow">Heat carried in the last tick, in full relay pump rates (2 = two relays' worth).</param>
/// <param name="ChargeFlow">Charge carried out to pylons, in full pylon hand-outs (1 = one pylon at full rate).</param>
/// <param name="OreFlow">Ore unloaded into the network on its way home: 1 just after a unit passed, fading to 0.</param>
public readonly record struct LinkRenderState(
    Position From,
    Position To,
    LinkType Type,
    bool IsAlive,
    float HeatFlow = 0f,
    bool FlowsFromTo = true,
    float ChargeFlow = 0f,
    bool ChargeFlowsFromTo = true,
    float OreFlow = 0f,
    bool OreFlowsFromTo = true);

public readonly record struct SpriteRenderState(
    Position Position,
    SpriteType Type,
    bool IsAlive);
