// ============================================================================
// EntityPs.hlsl — Entity layer pixel shader (tanks and projectiles)
// ============================================================================
//
// Draws entities procedurally at screen resolution, over the finished terrain
// frame. Each entity is a quad whose `local` coordinate is in world cells with
// +x along the entity's facing (tank hull, turret) or travel (projectile)
// direction, so shapes are signed-distance functions of `local` and stay sharp
// at any pixel scale.
//
// Output is premultiplied alpha (blend: One, InvSrcAlpha), so opaque bodies and
// additive glows share one pass: a glow adds colour with zero alpha.
//
// Shape kinds (color.a) and their params:
//   0 tank hull   — .xy = facing (cos, sin), .z = tread travel (cells),
//                   .w = state (0 normal, 1 hit flash, 2 spawn protection)
//   1 tank turret — .xy = barrel direction (cos, sin), .w = state as above
//   2 hot shot    — .x = trail length (cells), .y = core radius (cells)
//   3 foam blob   — .y = radius (cells)
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
//  13 coverage    — .x = radius (cells), .y = cooling strength (0..1), .z = phase, .w = 1 while the tank is off network
//  14 tether      — .x = length (cells, toward the node at +x), .y = cooling strength, .z = phase
//                   (machine flags: 1 stalled, 2 carried, 4 held back by a full base)
//
// The tank shapes stay inside the 7x7-cell collision footprint: tracks and hull
// cover the same 6x5 cells as the simulation's tank sprite.
// ============================================================================

// Shape kinds (color.a); EntityLayerBuilder's Kind* constants must match (EntityLayoutGuardTests).
static const float kKindTankHull = 0.0;
static const float kKindTankTurret = 1.0;
static const float kKindHotShot = 2.0;
static const float kKindFoam = 3.0;
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

// Machine flags (params.w); EntityLayerBuilder's MachineFlag* constants must match.
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

static const float2 kLightDir = float2(-0.55, -0.83);  // world-space direction towards the light (up-left)
static const float3 kOutlineColor = float3(0.035, 0.03, 0.025);
static const float3 kProtectedColor = float3(0.35, 0.90, 1.00);

float sdRoundBox(float2 p, float2 center, float2 halfSize, float radius)
{
    float2 q = abs(p - center) - halfSize + radius;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
}

float sdSegment(float2 p, float2 a, float2 b)
{
    float2 pa = p - a;
    float2 ba = b - a;
    float h = saturate(dot(pa, ba) / max(dot(ba, ba), 1e-6));
    return length(pa - ba * h);
}

// Anti-aliased coverage of the inside of a signed distance (aa = one pixel in cells).
float Coverage(float dist, float aa)
{
    return saturate(0.5 - dist / aa);
}

// Composite a premultiplied layer over the accumulated premultiplied result.
void Over(inout float4 dst, float3 rgb, float alpha)
{
    dst.rgb = rgb * alpha + dst.rgb * (1.0 - alpha);
    dst.a = alpha + dst.a * (1.0 - alpha);
}

// Light arriving in the entity's local frame.
float2 LocalLight(float2 facing)
{
    return float2(dot(kLightDir, facing), dot(kLightDir, float2(-facing.y, facing.x)));
}

float3 ApplyState(float3 rgb, float state)
{
    return state > 0.5 && state < 1.5 ? float3(1.0, 1.0, 1.0) : rgb;
}

float4 TankHull(float2 p, float3 team, float4 params, float aa)
{
    float2 facing = params.xy;
    float2 light = LocalLight(facing);
    float state = params.w;
    float4 result = float4(0.0, 0.0, 0.0, 0.0);

    static const float2 kHullCenter = float2(-0.5, 0.0);
    static const float2 kHullHalf = float2(2.45, 1.55);
    static const float2 kTrackHalf = float2(3.0, 0.52);
    static const float kTrackOffset = 1.98;

    // ---- Ground shadow: the silhouette, pushed away from the light ---------
    float2 shadowP = p + light * 0.75;
    float shadowDist = min(
        sdRoundBox(shadowP, kHullCenter, kHullHalf, 0.5),
        sdRoundBox(float2(shadowP.x, abs(shadowP.y)), float2(kHullCenter.x, kTrackOffset), kTrackHalf, 0.3));
    Over(result, float3(0.0, 0.0, 0.0), 0.42 * smoothstep(0.55, -0.35, shadowDist));

    // ---- Tracks --------------------------------------------------------------
    float2 trackP = float2(p.x, abs(p.y));
    float trackDist = sdRoundBox(trackP, float2(kHullCenter.x, kTrackOffset), kTrackHalf, 0.3);
    float tread = smoothstep(0.30, 0.70, abs(frac((p.x + params.z) * 1.25) - 0.5) * 2.0);
    float trackShade = 1.0 - 0.45 * smoothstep(0.15, 0.50, abs(trackP.y - kTrackOffset));
    float3 trackColor = lerp(float3(0.07, 0.07, 0.08), float3(0.27, 0.26, 0.25), tread) * trackShade;
    float3 outline = state > 1.5 ? kProtectedColor : kOutlineColor;
    Over(result, outline, Coverage(trackDist - 0.16, aa));
    Over(result, ApplyState(trackColor, state), Coverage(trackDist, aa));

    // ---- Hull ----------------------------------------------------------------
    float hullDist = sdRoundBox(p, kHullCenter, kHullHalf, 0.5);
    float2 e = float2(0.05, 0.0);
    float2 hullNormal = normalize(float2(
        sdRoundBox(p + e.xy, kHullCenter, kHullHalf, 0.5) - sdRoundBox(p - e.xy, kHullCenter, kHullHalf, 0.5),
        sdRoundBox(p + e.yx, kHullCenter, kHullHalf, 0.5) - sdRoundBox(p - e.yx, kHullCenter, kHullHalf, 0.5)) + 1e-5);
    float bevel = smoothstep(-0.75, -0.05, hullDist);
    float lit = dot(hullNormal, light);
    float3 hullColor = team * (0.80 + 0.42 * lit * bevel);
    hullColor += float3(0.16, 0.16, 0.16) * saturate(lit) * bevel;

    // Engine deck at the rear and a glacis line at the front.
    float deck = Coverage(sdRoundBox(p, float2(-2.05, 0.0), float2(0.42, 0.95), 0.12), aa);
    float louvre = smoothstep(0.25, 0.45, abs(frac(p.y * 2.2) - 0.5));
    hullColor = lerp(hullColor, team * lerp(0.30, 0.52, louvre), deck);
    float glacis = Coverage(abs(p.x - 1.25) - 0.06, aa) * step(abs(p.y), 1.2);
    hullColor = lerp(hullColor, team * 0.50, glacis * 0.8);

    Over(result, outline, Coverage(hullDist - 0.16, aa));
    Over(result, ApplyState(hullColor, state), Coverage(hullDist, aa));
    return result;
}

float4 TankTurret(float2 p, float3 team, float4 params, float aa)
{
    float2 light = LocalLight(params.xy);
    float state = params.w;
    float4 result = float4(0.0, 0.0, 0.0, 0.0);
    float3 outline = state > 1.5 ? kProtectedColor : kOutlineColor;

    // ---- Barrel --------------------------------------------------------------
    static const float2 kBarrelCenter = float2(2.35, 0.0);
    static const float2 kBarrelHalf = float2(2.0, 0.33);
    float barrelDist = sdRoundBox(p, kBarrelCenter, kBarrelHalf, 0.12);
    float barrelLit = 0.62 + 0.36 * saturate(-p.y / kBarrelHalf.y * sign(light.y) * 0.5 + 0.5);
    float3 barrelColor = lerp(team * 0.45, float3(0.34, 0.35, 0.37), 0.55) * barrelLit;
    // Bright muzzle tip: the aiming cue the old one-pixel barrel tip gave.
    barrelColor = lerp(barrelColor, float3(0.95, 0.92, 0.11), smoothstep(3.55, 3.75, p.x));
    Over(result, outline, Coverage(barrelDist - 0.14, aa));
    Over(result, ApplyState(barrelColor, state), Coverage(barrelDist, aa));

    // ---- Dome ----------------------------------------------------------------
    float domeDist = length(p) - 1.12;
    float2 domeNormal = p / max(length(p), 1e-4);
    float domeRise = saturate(length(p) / 1.12);
    float3 domeColor = team * (1.02 + 0.40 * dot(domeNormal, light) * domeRise);
    domeColor += float3(0.22, 0.22, 0.22) * smoothstep(0.55, 0.0, length(p - light * 0.42));
    float hatch = Coverage(abs(length(p + float2(0.25, 0.0)) - 0.42) - 0.05, aa);
    domeColor = lerp(domeColor, team * 0.45, hatch * 0.75);
    Over(result, outline, Coverage(domeDist - 0.15, aa));
    Over(result, ApplyState(domeColor, state), Coverage(domeDist, aa));
    return result;
}

// A glowing shot: white-hot head, a tail that cools from yellow to the shot's
// colour, and an additive halo around both.
float4 HotShot(float2 p, float3 body, float4 params, float aa)
{
    float trail = max(params.x, 0.01);
    float radius = params.y;
    float dist = sdSegment(p, float2(-trail, 0.0), float2(0.0, 0.0));
    float along = saturate(1.0 + p.x / trail);                 // 0 at the tail, 1 at the head
    float taper = lerp(0.35, 1.0, along);

    float core = Coverage(dist - radius * taper, aa) * lerp(0.25, 1.0, along);
    float3 coreColor = lerp(body, float3(1.0, 0.86, 0.42), along);
    coreColor = lerp(coreColor, float3(1.0, 0.98, 0.90), smoothstep(0.75, 1.0, along));

    float halo = exp(-max(dist - radius * 0.5, 0.0) * (2.4 / max(radius, 0.05))) * lerp(0.15, 1.0, along);
    float3 glow = lerp(body, float3(1.0, 0.55, 0.16), 0.6) * halo * 0.85;

    return float4(coreColor * core + glow, core);
}

float4 FoamBlob(float2 p, float3 body, float4 params, float aa)
{
    float radius = params.y;
    float dist = length(p) - radius;
    float4 result = float4(0.0, 0.0, 0.0, 0.0);
    Over(result, kOutlineColor, Coverage(dist - 0.12, aa) * 0.8);
    float3 lit = body * (0.85 + 0.35 * saturate(dot(p / max(radius, 1e-4), kLightDir)));
    Over(result, lit, Coverage(dist, aa));
    return result;
}

// Machine flags (params.w): bit 1 stalled, bit 2 carried, bit 4 held back by a full base.
bool MachineFlag(float flags, float bit)
{
    return fmod(floor(flags / bit), 2.0) > 0.5;
}

// A carried machine is a see-through ghost over the tank.
float4 Ghost(float4 c, float flags)
{
    return MachineFlag(flags, kMachineFlagCarried) ? c * 0.55 : c;
}

// Relay: a heat pump on a finned plate. Its core glows cyan once it is linked to a powered network, brighter and
// with a spinning ring while it pumps heat; an amber lamp shows a base too full to take more.
float4 Relay(float2 p, float4 params, float aa)
{
    float powered = params.x;
    float pump = saturate(params.y);
    float phase = params.z;
    float flags = params.w;
    float2 light = kLightDir;
    float4 result = float4(0.0, 0.0, 0.0, 0.0);

    // Radiator fins (a cross under the plate) and the plate, shadowed together.
    float2 q = abs(p);
    float fins = min(sdRoundBox(q, float2(0.0, 0.0), float2(3.05, 0.42), 0.2),
                     sdRoundBox(q, float2(0.0, 0.0), float2(0.42, 3.05), 0.2));
    float plate = sdRoundBox(p, float2(0.0, 0.0), float2(2.15, 2.15), 0.95);
    float body = min(fins, plate);
    float2 sp = abs(p + light * 0.6);
    float shadow = min(min(sdRoundBox(sp, float2(0.0, 0.0), float2(3.05, 0.42), 0.2),
                           sdRoundBox(sp, float2(0.0, 0.0), float2(0.42, 3.05), 0.2)),
                       sdRoundBox(p + light * 0.6, float2(0.0, 0.0), float2(2.15, 2.15), 0.95));
    Over(result, float3(0.0, 0.0, 0.0), 0.40 * smoothstep(0.5, -0.3, shadow));

    float ribs = smoothstep(0.3, 0.5, abs(frac(max(q.x, q.y) * 2.4) - 0.5));
    float3 finColor = lerp(float3(0.20, 0.22, 0.24), float3(0.36, 0.38, 0.40), ribs);
    Over(result, kOutlineColor, Coverage(fins - 0.14, aa));
    Over(result, finColor, Coverage(fins, aa));

    float2 n = p / max(length(p), 1e-4);
    float plateLit = 0.85 + 0.30 * dot(n, light) * smoothstep(0.6, 2.2, length(p));
    float3 plateColor = float3(0.27, 0.30, 0.33) * plateLit;
    Over(result, kOutlineColor, Coverage(plate - 0.14, aa));
    Over(result, plateColor, Coverage(plate, aa));

    // Ring around the core: dark when idle, cyan when linked, spinning segments while pumping.
    float3 cyan = float3(0.30, 0.88, 1.00);
    float angle = atan2(p.y, p.x);
    float segments = smoothstep(0.15, 0.35, abs(frac(angle / 6.2831853 * 6.0 + phase * (0.4 + 1.6 * pump)) - 0.5));
    float ringDist = abs(length(p) - 1.45) - 0.2;
    float3 ringColor = powered > 0.5
        ? lerp(cyan * 0.45, cyan * (0.8 + 0.5 * pump), lerp(1.0, segments, step(0.02, pump)))
        : float3(0.14, 0.20, 0.22);
    Over(result, ringColor, Coverage(ringDist, aa));

    // Core: pulses with the pumping rate.
    float coreDist = length(p) - 0.8;
    float pulse = 0.75 + 0.25 * sin(phase * (3.0 + 6.0 * pump));
    float3 coreColor = powered > 0.5
        ? lerp(cyan * 0.7, float3(0.92, 1.0, 1.0), pump * pulse)
        : float3(0.10, 0.13, 0.15);
    coreColor += float3(0.15, 0.15, 0.15) * smoothstep(0.5, 0.0, length(p + float2(0.25, 0.3)));
    Over(result, kOutlineColor, Coverage(coreDist - 0.12, aa));
    Over(result, coreColor, Coverage(coreDist, aa));

    // Additive glow while pumping.
    float halo = exp(-max(length(p) - 0.8, 0.0) * 1.6) * pump * powered * 0.55;
    result.rgb += cyan * halo * (1.0 - result.a * 0.5);

    if (MachineFlag(flags, kMachineFlagLimited))
    {
        float lamp = length(p - float2(1.55, -1.55)) - 0.38;
        Over(result, kOutlineColor, Coverage(lamp - 0.1, aa));
        Over(result, float3(1.0, 0.62, 0.12) * (0.75 + 0.25 * sin(phase * 8.0)), Coverage(lamp, aa));
    }
    return Ghost(result, flags);
}

// Harvester: a tracked crawler with a spinning cutter drum at the front (+x), a dirt hopper and a cab. The drum
// turns while it works; a blinking orange beacon means the ground is too hot and it has stalled.
float4 Harvester(float2 p, float4 params, float aa)
{
    float2 light = LocalLight(params.xy);
    float drum = params.z;
    float flags = params.w;
    bool stalled = MachineFlag(flags, kMachineFlagStalled);
    float4 result = float4(0.0, 0.0, 0.0, 0.0);

    static const float2 kBodyCenter = float2(-0.45, 0.0);
    static const float2 kBodyHalf = float2(2.25, 1.55);
    static const float2 kTrackHalf = float2(2.75, 0.6);
    static const float kTrackOffset = 2.15;
    static const float2 kDrumCenter = float2(2.45, 0.0);
    static const float2 kDrumHalf = float2(0.55, 2.55);

    // ---- Shadow -----------------------------------------------------------------
    float2 sp = p + light * 0.7;
    float shadow = min(min(
        sdRoundBox(sp, kBodyCenter, kBodyHalf, 0.5),
        sdRoundBox(float2(sp.x, abs(sp.y)), float2(kBodyCenter.x, kTrackOffset), kTrackHalf, 0.3)),
        sdRoundBox(sp, kDrumCenter, kDrumHalf, 0.35));
    Over(result, float3(0.0, 0.0, 0.0), 0.42 * smoothstep(0.55, -0.35, shadow));

    // ---- Tracks -----------------------------------------------------------------
    float2 tp = float2(p.x, abs(p.y));
    float trackDist = sdRoundBox(tp, float2(kBodyCenter.x, kTrackOffset), kTrackHalf, 0.3);
    float tread = smoothstep(0.3, 0.7, abs(frac((p.x + drum * 0.5) * 1.3) - 0.5) * 2.0);
    float3 trackColor = lerp(float3(0.07, 0.07, 0.08), float3(0.26, 0.25, 0.23), tread);
    Over(result, kOutlineColor, Coverage(trackDist - 0.15, aa));
    Over(result, trackColor, Coverage(trackDist, aa));

    // ---- Cutter drum --------------------------------------------------------------
    float drumDist = sdRoundBox(p, kDrumCenter, kDrumHalf, 0.35);
    float teeth = smoothstep(0.25, 0.45, abs(frac(p.y * 1.6 + drum) - 0.5));
    float drumRound = 0.55 + 0.45 * saturate(1.0 - abs(p.x - kDrumCenter.x) / kDrumHalf.x);
    float3 drumColor = lerp(float3(0.16, 0.16, 0.17), float3(0.55, 0.53, 0.50), teeth) * drumRound;
    Over(result, kOutlineColor, Coverage(drumDist - 0.15, aa));
    Over(result, drumColor, Coverage(drumDist, aa));

    // ---- Body ---------------------------------------------------------------------
    float bodyDist = sdRoundBox(p, kBodyCenter, kBodyHalf, 0.5);
    float2 e = float2(0.05, 0.0);
    float2 bodyNormal = normalize(float2(
        sdRoundBox(p + e.xy, kBodyCenter, kBodyHalf, 0.5) - sdRoundBox(p - e.xy, kBodyCenter, kBodyHalf, 0.5),
        sdRoundBox(p + e.yx, kBodyCenter, kBodyHalf, 0.5) - sdRoundBox(p - e.yx, kBodyCenter, kBodyHalf, 0.5)) + 1e-5);
    float bevel = smoothstep(-0.7, -0.05, bodyDist);
    float lit = dot(bodyNormal, light);
    float3 sand = float3(0.86, 0.60, 0.22);
    float3 bodyColor = sand * (0.80 + 0.40 * lit * bevel) + float3(0.12, 0.12, 0.12) * saturate(lit) * bevel;
    // Warning stripes along the front edge.
    float stripes = step(0.5, frac((p.x + p.y) * 1.4)) * Coverage(abs(p.x - 1.45) - 0.22, aa) * step(abs(p.y), 1.4);
    bodyColor = lerp(bodyColor, float3(0.10, 0.09, 0.08), stripes * 0.85);
    Over(result, kOutlineColor, Coverage(bodyDist - 0.15, aa));
    Over(result, bodyColor, Coverage(bodyDist, aa));

    // Hopper full of dirt in the middle.
    float hopper = sdRoundBox(p, float2(-0.15, 0.35), float2(1.05, 0.85), 0.25);
    float grain = 0.8 + 0.2 * frac(sin(dot(floor(p * 5.0), float2(12.9898, 78.233))) * 43758.5453);
    Over(result, float3(0.12, 0.08, 0.05), Coverage(hopper, aa));
    Over(result, float3(0.42, 0.27, 0.13) * grain, Coverage(hopper + 0.18, aa));

    // Cab with a window, at the rear.
    float cab = sdRoundBox(p, float2(-1.85, -0.75), float2(0.6, 0.55), 0.2);
    Over(result, kOutlineColor, Coverage(cab - 0.1, aa));
    Over(result, sand * 0.72, Coverage(cab, aa));
    float glass = sdRoundBox(p, float2(-1.6, -0.75), float2(0.25, 0.38), 0.1);
    Over(result, lerp(float3(0.12, 0.22, 0.30), float3(0.55, 0.75, 0.85), saturate(-light.x * 0.5 + 0.5)), Coverage(glass, aa));

    // Beacon: steady green while working, orange while stalled.
    float beacon = length(p - float2(-1.85, 0.75)) - 0.3;
    float3 beaconColor = stalled ? float3(1.0, 0.55, 0.10) : float3(0.35, 1.0, 0.45);
    Over(result, kOutlineColor, Coverage(beacon - 0.1, aa));
    Over(result, beaconColor, Coverage(beacon, aa));
    if (stalled)
        result.rgb += float3(1.0, 0.5, 0.1) * exp(-max(beacon, 0.0) * 3.0) * 0.5;

    return Ghost(result, flags);
}

// The ground a harvester senses: a dashed ring, green while it can work, amber as it nears its stall temperature,
// and a solid pulsing orange once it is too hot (a shape cue as well as a colour one).
float4 SenseRing(float2 p, float4 params, float aa)
{
    float heat = params.x;
    float radius = params.y;
    float phase = params.z;
    bool tooHot = heat >= 1.0;
    float3 color = tooHot ? float3(1.0, 0.55, 0.10)
        : lerp(float3(0.35, 1.0, 0.47), float3(1.0, 0.84, 0.24), smoothstep(0.6, 0.95, heat));
    float ring = abs(length(p) - radius) - 0.12;
    float angle = atan2(p.y, p.x) / 6.2831853;
    float dash = tooHot ? 1.0 : smoothstep(0.2, 0.3, abs(frac(angle * 16.0 + phase * 0.15) - 0.5));
    float alpha = Coverage(ring, aa) * dash * (tooHot ? 0.75 + 0.25 * sin(phase * 6.0) : 0.7);
    float4 result = float4(0.0, 0.0, 0.0, 0.0);
    Over(result, color, alpha);
    return result;
}

// A link beam. Live: a thin cyan line with a slow shimmer. Blocked: a dim red dashed line. Carrying heat: an
// orange plasma beam that wobbles, with bright pulses racing toward the base and a glow that grows with the flow.
float4 LinkBeam(float2 p, float4 params, float aa)
{
    float len = params.x;
    float heat = params.y;
    float phase = params.z;
    bool blocked = params.w > 0.5;
    float endFade = smoothstep(-0.2, 1.2, p.x) * smoothstep(len + 0.2, len - 1.2, p.x);
    float4 result = float4(0.0, 0.0, 0.0, 0.0);

    if (blocked)
    {
        float d = sdSegment(p, float2(0.0, 0.0), float2(len, 0.0));
        float dash = smoothstep(0.35, 0.45, abs(frac(p.x * 0.45) - 0.5));
        Over(result, float3(0.85, 0.22, 0.18), Coverage(d - 0.09, aa) * dash * 0.55 * endFade);
        return result;
    }

    // Even a trickle of heat should read clearly: strength starts at 0.4 and grows with the square root of the flow.
    float h = saturate(0.4 + 0.6 * sqrt(max(heat, 0.0)));
    float hot = step(0.01, heat);
    // The beam wobbles more the more heat it carries.
    float wobble = (sin(p.x * 1.1 - phase * 6.0) + 0.5 * sin(p.x * 2.7 + phase * 9.0)) * 0.10 * h;
    float d = sdSegment(float2(p.x, p.y - wobble), float2(0.0, 0.0), float2(len, 0.0));

    if (hot < 0.5)
    {
        // Idle link: a see-through filament of light with motes drifting along it, no solid line.
        float3 cyan = float3(0.30, 0.85, 1.00);
        float shimmer = pow(saturate(1.0 - abs(frac(p.x * 0.08 - phase * 0.35) - 0.5) * 2.0), 8.0);
        float motes = pow(saturate(1.0 - abs(frac(p.x * 0.25 - phase * 0.6) - 0.5) * 2.0), 12.0);
        float filament = exp(-d * d * 30.0) * (0.35 + 0.5 * shimmer + 0.6 * motes);
        float haze = exp(-d * 2.8) * (0.08 + 0.20 * shimmer + 0.15 * motes);
        float3 light = cyan * (filament + haze) * endFade;
        return float4(light, saturate(filament * 0.25) * endFade);
    }

    float flow = min(h + max(heat - 1.0, 0.0) * 0.5, 2.5);
    // Pulses travelling toward +x; spacing and speed fixed so their motion reads as direction, brightness as amount.
    float travel = p.x * 0.32 - phase * (1.4 + 0.6 * h);
    float pulse = pow(saturate(1.0 - abs(frac(travel) - 0.5) * 2.0), 6.0);
    float fine = pow(saturate(1.0 - abs(frac(travel * 3.1 + 0.37) - 0.5) * 2.0), 10.0) * 0.5;
    float energy = saturate(0.35 + 0.65 * (pulse + fine));

    float coreWidth = 0.10 + 0.10 * h + 0.18 * pulse * h;
    float3 orange = float3(1.0, 0.42, 0.08);
    float3 yellow = float3(1.0, 0.82, 0.35);
    float3 white = float3(1.0, 0.97, 0.88);
    float3 coreColor = lerp(lerp(orange, yellow, energy), white, pulse * h);
    Over(result, coreColor, Coverage(d - coreWidth, aa) * endFade);

    // Heat haze: an additive glow whose reach grows with the flow, brighter at each pulse.
    float glowReach = 1.8 + 1.6 * flow;
    float glow = exp(-d * (3.0 / glowReach)) * (0.30 + 0.55 * flow * (0.4 + 0.6 * pulse));
    result.rgb += lerp(orange, yellow, pulse) * glow * endFade;
    return result;
}

static const float3 kChargeBlue = float3(0.30, 0.55, 1.00);
static const float3 kChargeViolet = float3(0.62, 0.45, 1.00);

// A charge beam: a thin electric-blue arc that jitters, with crisp sparks racing toward +x. It is drawn beside heat
// beams on the same links, so it is deliberately narrower and cooler-coloured.
float4 ChargeBeam(float2 p, float4 params, float aa)
{
    float len = params.x;
    float flow = saturate(params.y);
    float phase = params.z;
    float endFade = smoothstep(-0.2, 1.0, p.x) * smoothstep(len + 0.2, len - 1.0, p.x);
    float strength = 0.45 + 0.55 * sqrt(flow);

    // Electric jitter: fast, small, piecewise so it reads as an arc rather than a wave.
    float t = floor(phase * 14.0);
    float n = frac(sin(floor(p.x * 1.5) * 17.13 + t * 3.7) * 43758.5);
    float n2 = frac(sin(floor(p.x * 1.5 + 1.0) * 17.13 + t * 3.7) * 43758.5);
    float jitter = (lerp(n, n2, frac(p.x * 1.5)) - 0.5) * 0.35 * strength;
    float d = sdSegment(float2(p.x, p.y - jitter), float2(0.0, 0.0), float2(len, 0.0));

    float travel = p.x * 0.45 - phase * 2.4;
    float spark = pow(saturate(1.0 - abs(frac(travel) - 0.5) * 2.0), 10.0);
    float3 color = lerp(kChargeBlue, float3(0.85, 0.92, 1.0), spark);

    float4 result = float4(0.0, 0.0, 0.0, 0.0);
    Over(result, color, Coverage(d - (0.06 + 0.12 * spark), aa) * endFade * strength);
    float glow = exp(-d * 2.2) * (0.18 + 0.6 * spark) * strength;
    result.rgb += lerp(kChargeBlue, kChargeViolet, 0.35) * glow * endFade;
    return result;
}

// Pylon: a mast on a three-legged base with a charge crystal on top. The crystal glows blue once the pylon is on a
// powered network, and arcs crackle around it while it hands out charge.
float4 Pylon(float2 p, float4 params, float aa)
{
    float powered = params.x;
    float giving = params.y;
    float phase = params.z;
    float flags = params.w;
    float4 result = float4(0.0, 0.0, 0.0, 0.0);

    // Three legs at 120 degrees, plus a hub; shadowed together.
    float legs = 1e5, legsShadow = 1e5;
    float2 sp = p + kLightDir * 0.6;
    [unroll] for (int i = 0; i < 3; i++)
    {
        float a = 1.5708 + i * 2.0944;
        float2 tip = float2(cos(a), sin(a)) * 3.0;
        legs = min(legs, sdSegment(p, float2(0.0, 0.0), tip) - 0.38);
        legsShadow = min(legsShadow, sdSegment(sp, float2(0.0, 0.0), tip) - 0.38);
    }
    float hub = length(p) - 1.55;
    Over(result, float3(0.0, 0.0, 0.0), 0.40 * smoothstep(0.5, -0.3, min(legsShadow, length(sp) - 1.55)));

    Over(result, kOutlineColor, Coverage(legs - 0.14, aa));
    Over(result, float3(0.26, 0.28, 0.32), Coverage(legs, aa));
    float2 nrm = p / max(length(p), 1e-4);
    float3 hubColor = float3(0.30, 0.33, 0.38) * (0.85 + 0.3 * dot(nrm, kLightDir) * smoothstep(0.4, 1.5, length(p)));
    Over(result, kOutlineColor, Coverage(hub - 0.14, aa));
    Over(result, hubColor, Coverage(hub, aa));

    // Crystal: a diamond seen from above.
    float crystal = (abs(p.x) + abs(p.y)) * 0.7071 - 0.75;
    float facet = step(0.0, p.x * kLightDir.x + p.y * kLightDir.y);
    float pulse = 0.7 + 0.3 * sin(phase * (giving > 0.5 ? 9.0 : 2.5));
    float3 crystalColor = powered > 0.5
        ? lerp(kChargeBlue * 0.75, float3(0.80, 0.90, 1.0), facet * 0.5 + 0.4 * giving * pulse)
        : float3(0.12, 0.14, 0.20);
    Over(result, kOutlineColor, Coverage(crystal - 0.12, aa));
    Over(result, crystalColor, Coverage(crystal, aa));

    if (powered > 0.5)
    {
        float halo = exp(-max(length(p) - 0.6, 0.0) * 1.8) * (0.25 + 0.45 * giving * pulse);
        result.rgb += kChargeBlue * halo;
        if (giving > 0.5)
        {
            // Arcs flickering around the hub rim.
            float angle = atan2(p.y, p.x) / 6.2831853;
            float t = floor(phase * 12.0);
            float arcs = step(0.72, frac(sin(floor(angle * 9.0) * 12.7 + t * 5.3) * 43758.5));
            float rim = exp(-abs(length(p) - 2.0) * 6.0);
            result.rgb += float3(0.75, 0.85, 1.0) * rim * arcs * 0.8;
        }
    }
    return Ghost(result, flags);
}

// Battery gauge: a pill with a fill bar; blue when healthy, amber below half, blinking red when low and heading home.
// A bright running edge shows it is being charged.
float4 Battery(float2 p, float4 params, float aa)
{
    float fill = saturate(params.x);
    bool returning = params.y > 0.5;
    float phase = params.z;
    bool charging = params.w > 0.5;
    float4 result = float4(0.0, 0.0, 0.0, 0.0);

    static const float2 kHalf = float2(2.0, 0.5);
    float shell = sdRoundBox(p, float2(0.0, 0.0), kHalf, 0.45);
    Over(result, kOutlineColor, Coverage(shell - 0.14, aa) * 0.85);
    Over(result, float3(0.08, 0.09, 0.11), Coverage(shell, aa) * 0.85);

    float right = -kHalf.x + 0.18 + (kHalf.x - 0.18) * 2.0 * fill;
    float bar = max(sdRoundBox(p, float2(0.0, 0.0), kHalf - 0.18, 0.3), p.x - right);
    float3 color = fill > 0.5 ? kChargeBlue : lerp(float3(1.0, 0.25, 0.15), float3(1.0, 0.72, 0.18), saturate(fill * 2.0 - 0.3));
    float blink = returning && !charging ? 0.55 + 0.45 * step(0.5, frac(phase * 2.0)) : 1.0;
    Over(result, color * blink, Coverage(bar, aa));
    if (charging)
    {
        float run = pow(saturate(1.0 - abs(frac(p.x * 0.35 - phase * 1.5) - 0.5) * 2.0), 8.0);
        result.rgb += float3(0.8, 0.9, 1.0) * run * Coverage(bar, aa) * 0.7;
    }
    return result;
}

// Placement preview: a translucent stream of energy from the carried machine to the network point it would join.
// It has no hard edge. Two soft, interleaved strands of light drift toward the network, with a lock-on ring
// where it would connect. Past a blocking cell the stream turns red and frays, with a glowing cross on the block.
// Too far: a short amber feeler that fades out.
float4 PreviewBeam(float2 p, float4 params, float aa)
{
    float len = params.x;
    float status = params.y;
    float phase = params.z;
    float blockAt = params.w;
    float startFade = smoothstep(-0.5, 2.5, p.x);
    float endFade = smoothstep(len + 0.5, len - 1.5, p.x);

    float3 good = float3(0.40, 1.00, 0.62);
    float3 bad = float3(1.00, 0.28, 0.20);
    float3 far = float3(1.00, 0.75, 0.30);

    // Two strands winding around the line of sight, drifting toward +x.
    float drift = phase * 3.0;
    float s1 = p.y - 0.45 * sin(p.x * 0.55 - drift);
    float s2 = p.y + 0.45 * sin(p.x * 0.55 - drift + 1.3);
    float strands = exp(-s1 * s1 * 9.0) + exp(-s2 * s2 * 9.0);
    float haze = exp(-p.y * p.y * 1.4);

    // Packets of energy travelling toward the network.
    float packets = pow(saturate(1.0 - abs(frac(p.x * 0.18 - phase * 0.9) - 0.5) * 2.0), 4.0);

    float pastBlock = status > 0.5 && status < 1.5 ? smoothstep(blockAt - 0.6, blockAt + 0.6, p.x) : 0.0;
    float3 color = status > 1.5 ? far : lerp(good, bad, pastBlock);
    // Past the block the stream frays: it breaks into flickering fragments.
    float fray = lerp(1.0, step(0.45, frac(sin(floor(p.x * 1.3) * 91.7 + floor(phase * 10.0) * 7.1) * 43758.5)) * 0.6, pastBlock);
    float distanceFade = status > 1.5 ? saturate(1.0 - p.x / max(len, 1.0)) : 1.0;

    float intensity = (strands * (0.45 + 0.75 * packets) * 0.75 + haze * (0.14 + 0.26 * packets)) * fray;
    intensity *= startFade * endFade * distanceFade;

    float4 result = float4(color * intensity, intensity * 0.35);

    // Lock-on ring at the network point, turning slowly.
    if (status < 0.5)
    {
        float2 q = p - float2(len, 0.0);
        float r = length(q);
        float angle = atan2(q.y, q.x) / 6.2831853 + phase * 0.25;
        float ticks = smoothstep(0.25, 0.35, abs(frac(angle * 4.0) - 0.5));
        float ring = exp(-abs(r - 1.6) * 7.0) * ticks * (0.6 + 0.4 * sin(phase * 5.0));
        result.rgb += good * ring * 0.9;
        result.a += ring * 0.25;
    }
    // Glowing cross on the first blocking cell.
    if (status > 0.5 && status < 1.5)
    {
        float2 q = p - float2(blockAt, 0.0);
        float2 rq = float2(q.x + q.y, q.x - q.y) * 0.7071;
        float cross = max(Coverage(max(abs(rq.x) - 1.1, abs(rq.y) - 0.16), aa),
                          Coverage(max(abs(rq.y) - 1.1, abs(rq.x) - 0.16), aa));
        float pulse = 0.7 + 0.3 * sin(phase * 8.0);
        Over(result, bad * pulse, cross * 0.9);
        result.rgb += bad * exp(-length(q) * 1.5) * 0.5 * pulse;
    }
    return result;
}

// The ground a carried relay would cool (a square) or a carried pylon would reach (a circle): a faint tinted fill
// with a soft glowing rim. Ripples run inward for a relay (heat drawn in) and outward for a pylon (charge sent out).
float4 ReachArea(float2 p, float3 tint, float4 params, float aa)
{
    float reach = params.x;
    bool circle = params.y > 0.5;
    float phase = params.z;
    // Very rounded corners and no hard edge, so it reads as a field rather than a box.
    float d = circle ? length(p) - reach : sdRoundBox(p, float2(0.0, 0.0), float2(reach, reach), reach * 0.55);
    float inside = smoothstep(1.5, -2.5, d);

    // A soft rim that shimmers along its length.
    float angle = atan2(p.y, p.x);
    float shimmer = 0.6 + 0.4 * sin(angle * 7.0 + phase * 1.7) * sin(angle * 3.0 - phase * 1.1);
    float rim = exp(-abs(d) * 1.2) * shimmer;

    // Round ripples: inward for a relay, outward for a pylon, fading toward the centre.
    float dir = circle ? -1.0 : 1.0;
    float ripple = pow(saturate(1.0 - abs(frac(length(p) * 0.22 + dir * phase * 0.5) - 0.5) * 2.0), 5.0);
    float falloff = smoothstep(0.0, reach, length(p));

    // Reach zero well inside the quad (which extends reach + 6), so its square outline never shows.
    float quadFade = smoothstep(reach + 5.5, reach + 2.5, max(abs(p.x), abs(p.y)));
    float glow = (rim * 0.30 + inside * falloff * (0.04 + 0.10 * ripple)) * quadFade;
    return float4(tint * glow, (inside * falloff * 0.05 + rim * 0.05) * quadFade);
}

// Network coverage around one node: a barely-there cool tint inside and a soft, slowly breathing rim, so the
// territory reads without covering the terrain. Weaker nodes (more hops out) are fainter. While the tank is off
// network the rims brighten, showing the way back.
float4 Coverage(float2 p, float3 tint, float4 params, float aa)
{
    float radius = params.x;
    float strength = saturate(params.y);
    float phase = params.z;
    float emphasis = params.w;
    float r = length(p);
    float d = r - radius;

    float inside = smoothstep(1.0, -3.0, d);
    float angle = atan2(p.y, p.x);
    float breathe = 0.75 + 0.25 * sin(phase * 1.3 + angle * 2.0);
    float rim = exp(-abs(d) * 1.4) * breathe;
    float quadFade = smoothstep(radius + 4.0, radius + 2.0, max(abs(p.x), abs(p.y)));

    float level = (0.35 + 0.65 * strength) * (1.0 + 1.2 * emphasis);
    float3 cool = lerp(tint, float3(0.70, 0.95, 1.0), 0.3);
    float glow = (rim * 0.16 + inside * 0.025) * level * quadFade;
    return float4(cool * glow, (rim * 0.04 + inside * 0.02) * level * quadFade);
}

// Tether: the tank's heat being carried to the covering node. A faint icy thread with warm motes leaving the tank
// (at x = 0) and cooling as they travel toward the node (+x).
float4 Tether(float2 p, float4 params, float aa)
{
    float len = params.x;
    float strength = saturate(params.y);
    float phase = params.z;
    float startFade = smoothstep(1.0, 3.5, p.x);
    float endFade = smoothstep(len + 0.5, len - 2.0, p.x);
    float sway = sin(p.x * 0.35 - phase * 2.0) * 0.25;
    float d = abs(p.y - sway);

    float thread = exp(-d * d * 40.0) * 0.35;
    float travel = p.x * 0.3 - phase * 2.2;
    float mote = pow(saturate(1.0 - abs(frac(travel) - 0.5) * 2.0), 10.0) * exp(-d * d * 10.0);
    float along = saturate(p.x / max(len, 1.0));
    float3 moteColor = lerp(float3(1.0, 0.55, 0.20), float3(0.55, 0.90, 1.0), along);
    float3 threadColor = float3(0.45, 0.85, 1.0);

    float level = (0.5 + 0.5 * strength) * startFade * endFade;
    float3 rgb = (threadColor * thread + moteColor * mote * 0.9) * level;
    return float4(rgb, saturate(thread * 0.5 + mote * 0.3) * level);
}

float4 main(PSIn input) : SV_Target
{
    float2 p = input.local;
    float aa = max(length(fwidth(p)) * 0.7071, 1e-4);
    float kind = input.color.a;

    if (kind < kKindTankHull + 0.5)
        return TankHull(p, input.color.rgb, input.params, aa);
    if (kind < kKindTankTurret + 0.5)
        return TankTurret(p, input.color.rgb, input.params, aa);
    if (kind < kKindHotShot + 0.5)
        return HotShot(p, input.color.rgb, input.params, aa);
    if (kind < kKindFoam + 0.5)
        return FoamBlob(p, input.color.rgb, input.params, aa);
    if (kind < kKindRelay + 0.5)
        return Relay(p, input.params, aa);
    if (kind < kKindHarvester + 0.5)
        return Harvester(p, input.params, aa);
    if (kind < kKindSenseRing + 0.5)
        return SenseRing(p, input.params, aa);
    if (kind < kKindLink + 0.5)
        return LinkBeam(p, input.params, aa);
    if (kind < kKindChargeBeam + 0.5)
        return ChargeBeam(p, input.params, aa);
    if (kind < kKindPylon + 0.5)
        return Pylon(p, input.params, aa);
    if (kind < kKindBattery + 0.5)
        return Battery(p, input.params, aa);
    if (kind < kKindPreviewBeam + 0.5)
        return PreviewBeam(p, input.params, aa);
    if (kind < kKindReachArea + 0.5)
        return ReachArea(p, input.color.rgb, input.params, aa);
    if (kind < kKindCoverage + 0.5)
        return Coverage(p, input.color.rgb, input.params, aa);
    return Tether(p, input.params, aa); // kKindTether
}
