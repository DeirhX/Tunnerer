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
//   8 battery     — .x = capacitor fill (0..1)
//   9 preview beam — .x = length (cells, toward the network point at +x), .y = 0 would link, 1 blocked,
//                   2 too far, .z = phase, .w = distance to the first blocking cell
//  10 reach area  — .x = reach (cells), .y = 0 square (relay cooling), 1 circle (turret range), .z = phase
//  11 network coverage — .x = radius (cells), .y = cooling strength (0..1), .z = phase, .w = 1 while the tank is off network
//  12 tether      — .x = length (cells, toward the node at +x), .y = cooling strength, .z = phase
//  13 radar blip  — .x = life (1 just found, 0 gone), .y = echo (RadarEcho: 0 solid, 1 metal, 2 cavity, 3 heat,
//                   4 life, 5 ore),
//                   .z = phase, .w = flash (1 just found, 0 after a few ticks);
//                   color.r = what heat left of the ray (0..1), color.g = a per-cell seed
//  14 radar pulse — .x = front radius (cells), .y = fade (1 just sent, 0 at full range)
//                   (machine flags: 1 stalled, 2 carried, 4 held back by a full base)
//  18 emberite  — .x = charge (1 carrying its heat, 0 spent), .y = burrowing (1 in soil: a dim glow under the
//                   ground), .z = animation phase (the wriggle), .w = 0. With .w = 1 the quad is a nest's
//                   resting embers instead: .x = 1 while creatures rest in it, .y = the reach of the embers (cells).
//  19 gatling   — .xy = facing (cos, sin), .z = drum phase (its barrels turn with it), .w = machine flags
//  16 power label — .xy = packed 5x7 glyph bitmap (low 20 bits, then high 15), .z = dots per cell
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
static const float kKindBattery = 8.0;
static const float kKindPreviewBeam = 9.0;
static const float kKindReachArea = 10.0;
static const float kKindCoverage = 11.0;
static const float kKindTether = 12.0;
static const float kKindRadarBlip = 13.0;
static const float kKindRadarPulse = 14.0;
static const float kKindGenerator = 15.0;
static const float kKindPowerLabel = 16.0;
static const float kKindTurret = 17.0;
static const float kKindEmberite = 18.0;
static const float kKindGatling = 19.0;

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
