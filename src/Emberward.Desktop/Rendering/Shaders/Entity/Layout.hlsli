// ============================================================================
// Entity/Layout.hlsli — Entity vertex layout, shape kinds and flags
// ============================================================================
//
// The contract with the C# side (Rendering/Entities): the vertex the pixel shader
// receives, the shape kinds and the machine flags. EntityShape's Kind* and
// MachineFlag* constants must match the ones here (EntityLayoutGuardTests).
//
// Shape kinds (color.a) and their params:
//   0 tank hull   — .xy = facing (cos, sin), .z = tread travel (cells),
//                   .w = state (0 normal, 1 hit flash, 2 spawn protection)
//   1 tank turret — .xy = barrel direction (cos, sin), .w = state as above
//   2 hot shot    — .x = trail length (cells), .y = core radius (cells)
//   3 ore beam    — .x = length (cells, along +x), .y = ore flow (1 just after a unit passed, fading to 0),
//                   .z = phase, .w = transition. Ore flows toward +x (the base).
//   4 relay       — .x = powered (0/1), .y = pump fraction, .z = animation phase (s), .w = machine flags
//   5 harvester   — .xy = facing (cos, sin), .z = drum/tread phase, .w = machine flags
//   6 sense ring  — .x = ground heat / stall temperature, .y = radius (cells), .z = phase, .w = machine flags
//   7 link        — .x = length (cells, along +x), .y = heat flow (relay pump rates), .z = phase,
//                   .w = 0 live, 1 blocked. Heat flows toward +x (the base).
//   8 charge beam — .x = length (cells, along +x), .y = charge flow (pylon hand-outs), .z = phase.
//                   Charge flows toward +x (a pylon or a battery).
//   9 pylon       — .x = powered (0/1), .y = handing out charge (0/1), .z = phase, .w = machine flags
//  10 battery     — .x = fill (0..1), .y = heading home to recharge (0/1), .z = phase, .w = charging (0/1)
//  11 preview beam — .x = length (cells, toward the network point at +x), .y = 0 would link, 1 blocked,
//                   2 too far, .z = phase, .w = distance to the first blocking cell
//  12 reach area  — .x = reach (cells), .y = 0 square (relay cooling), 1 circle (pylon), .z = phase
//  13 network coverage — .x = radius (cells), .y = cooling strength (0..1), .z = phase, .w = 1 while the tank is off network
//  14 tether      — .x = length (cells, toward the node at +x), .y = cooling strength, .z = phase
//  15 radar blip  — .x = life (1 just found, 0 gone), .y = echo (RadarEcho: 0 solid, 1 metal, 2 cavity, 3 heat,
//                   4 life, 5 ore),
//                   .z = phase, .w = flash (1 just found, 0 after a few ticks);
//                   color.r = what heat left of the ray (0..1), color.g = a per-cell seed
//  16 radar pulse — .x = front radius (cells), .y = fade (1 just sent, 0 at full range)
//                   (machine flags: 1 stalled, 2 carried, 4 held back by a full base)
//  18 power label — .xy = packed 5x7 glyph bitmap (low 20 bits, then high 15), .z = dots per cell
//                   (0 for a solid bar), .w = opacity
//
// color.rgb is the team colour (tank hull, turret), the body colour (hot shot),
// the tint (reach area, coverage, power label) or data (radar blip). Every other kind owns its colours
// here in the shader; the C# side sends black for them.
// ============================================================================

#ifndef EMBERWARD_ENTITY_LAYOUT_HLSLI
#define EMBERWARD_ENTITY_LAYOUT_HLSLI

// Shape kinds (color.a); EntityShape's Kind* constants must match (EntityLayoutGuardTests).
static const float kKindTankHull = 0.0;
static const float kKindTankTurret = 1.0;
static const float kKindHotShot = 2.0;
static const float kKindOreBeam = 3.0;
static const float kKindRelay = 4.0;
static const float kKindHarvester = 5.0;
static const float kKindSenseRing = 6.0;
static const float kKindLink = 7.0;
static const float kKindChargeBeam = 8.0;
static const float kKindPylon = 9.0;
static const float kKindBattery = 10.0;
static const float kKindPreviewBeam = 11.0;
static const float kKindReachArea = 12.0;
static const float kKindCoverage = 13.0;
static const float kKindTether = 14.0;
static const float kKindRadarBlip = 15.0;
static const float kKindRadarPulse = 16.0;
static const float kKindGenerator = 17.0;
static const float kKindPowerLabel = 18.0;
static const float kKindTurret = 19.0;

// Machine flags (params.w); EntityShape's MachineFlag* constants must match.
static const float kMachineFlagStalled = 1.0;
static const float kMachineFlagCarried = 2.0;
static const float kMachineFlagLimited = 4.0;

struct PSIn
{
    float4 pos : SV_POSITION;
    float2 local : TEXCOORD0;
    float4 color : COLOR0;
    float4 params : TEXCOORD1;
};

#endif // EMBERWARD_ENTITY_LAYOUT_HLSLI
