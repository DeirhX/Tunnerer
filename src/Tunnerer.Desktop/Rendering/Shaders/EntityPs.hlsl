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
//                   (machine flags: 1 stalled, 2 carried, 4 held back by a full base)
//
// The tank shapes stay inside the 7x7-cell collision footprint: tracks and hull
// cover the same 6x5 cells as the simulation's tank sprite.
// ============================================================================

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
    return MachineFlag(flags, 2.0) ? c * 0.55 : c;
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

    if (MachineFlag(flags, 4.0))
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
    bool stalled = MachineFlag(flags, 1.0);
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
        float3 cyan = float3(0.30, 0.85, 1.00);
        float shimmer = pow(saturate(1.0 - abs(frac(p.x * 0.08 - phase * 0.35) - 0.5) * 2.0), 8.0);
        Over(result, cyan * (0.65 + 0.6 * shimmer), Coverage(d - 0.09, aa) * 0.7 * endFade);
        result.rgb += cyan * exp(-d * 3.5) * (0.10 + 0.25 * shimmer) * endFade;
        return result;
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

float4 main(PSIn input) : SV_Target
{
    float2 p = input.local;
    float aa = max(length(fwidth(p)) * 0.7071, 1e-4);
    float kind = input.color.a;

    if (kind < 0.5)
        return TankHull(p, input.color.rgb, input.params, aa);
    if (kind < 1.5)
        return TankTurret(p, input.color.rgb, input.params, aa);
    if (kind < 2.5)
        return HotShot(p, input.color.rgb, input.params, aa);
    if (kind < 3.5)
        return FoamBlob(p, input.color.rgb, input.params, aa);
    if (kind < 4.5)
        return Relay(p, input.params, aa);
    if (kind < 5.5)
        return Harvester(p, input.params, aa);
    if (kind < 6.5)
        return SenseRing(p, input.params, aa);
    return LinkBeam(p, input.params, aa);
}
