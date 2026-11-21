// ============================================================================
// Entity/Machines.hlsli — Relay, harvester, turret, gatling, generator and capacitor gauge
// ============================================================================
//
// Machine bodies. A carried machine is drawn as a see-through ghost. The shader
// owns the machines' colours.
// ============================================================================

#ifndef EMBERWARD_ENTITY_MACHINES_HLSLI
#define EMBERWARD_ENTITY_MACHINES_HLSLI

#include "Layout.hlsli"
#include "Common.hlsli"

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
    Over(result, kOutlineColor, FillAlpha(fins - 0.14, aa));
    Over(result, finColor, FillAlpha(fins, aa));

    float2 n = p / max(length(p), 1e-4);
    float plateLit = 0.85 + 0.30 * dot(n, light) * smoothstep(0.6, 2.2, length(p));
    float3 plateColor = float3(0.27, 0.30, 0.33) * plateLit;
    Over(result, kOutlineColor, FillAlpha(plate - 0.14, aa));
    Over(result, plateColor, FillAlpha(plate, aa));

    // Ring around the core: dark when idle, cyan when linked, spinning segments while pumping.
    float3 cyan = float3(0.30, 0.88, 1.00);
    float angle = atan2(p.y, p.x);
    float segments = smoothstep(0.15, 0.35, abs(frac(angle / 6.2831853 * 6.0 + phase * (0.4 + 1.6 * pump)) - 0.5));
    float ringDist = abs(length(p) - 1.45) - 0.2;
    float3 ringColor = powered > 0.5
        ? lerp(cyan * 0.45, cyan * (0.8 + 0.5 * pump), lerp(1.0, segments, step(0.02, pump)))
        : float3(0.14, 0.20, 0.22);
    Over(result, ringColor, FillAlpha(ringDist, aa));

    // Core: pulses with the pumping rate.
    float coreDist = length(p) - 0.8;
    float pulse = 0.75 + 0.25 * sin(phase * (3.0 + 6.0 * pump));
    float3 coreColor = powered > 0.5
        ? lerp(cyan * 0.7, float3(0.92, 1.0, 1.0), pump * pulse)
        : float3(0.10, 0.13, 0.15);
    coreColor += float3(0.15, 0.15, 0.15) * smoothstep(0.5, 0.0, length(p + float2(0.25, 0.3)));
    Over(result, kOutlineColor, FillAlpha(coreDist - 0.12, aa));
    Over(result, coreColor, FillAlpha(coreDist, aa));

    // Additive glow while pumping.
    float halo = exp(-max(length(p) - 0.8, 0.0) * 1.6) * pump * powered * 0.55;
    result.rgb += cyan * halo * (1.0 - result.a * 0.5);

    if (MachineFlag(flags, kMachineFlagLimited))
    {
        float lamp = length(p - float2(1.55, -1.55)) - 0.38;
        Over(result, kOutlineColor, FillAlpha(lamp - 0.1, aa));
        Over(result, float3(1.0, 0.62, 0.12) * (0.75 + 0.25 * sin(phase * 8.0)), FillAlpha(lamp, aa));
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
    Over(result, kOutlineColor, FillAlpha(trackDist - 0.15, aa));
    Over(result, trackColor, FillAlpha(trackDist, aa));

    // ---- Cutter drum --------------------------------------------------------------
    float drumDist = sdRoundBox(p, kDrumCenter, kDrumHalf, 0.35);
    float teeth = smoothstep(0.25, 0.45, abs(frac(p.y * 1.6 + drum) - 0.5));
    float drumRound = 0.55 + 0.45 * saturate(1.0 - abs(p.x - kDrumCenter.x) / kDrumHalf.x);
    float3 drumColor = lerp(float3(0.16, 0.16, 0.17), float3(0.55, 0.53, 0.50), teeth) * drumRound;
    Over(result, kOutlineColor, FillAlpha(drumDist - 0.15, aa));
    Over(result, drumColor, FillAlpha(drumDist, aa));

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
    float stripes = step(0.5, frac((p.x + p.y) * 1.4)) * FillAlpha(abs(p.x - 1.45) - 0.22, aa) * step(abs(p.y), 1.4);
    bodyColor = lerp(bodyColor, float3(0.10, 0.09, 0.08), stripes * 0.85);
    Over(result, kOutlineColor, FillAlpha(bodyDist - 0.15, aa));
    Over(result, bodyColor, FillAlpha(bodyDist, aa));

    // Hopper full of dirt in the middle.
    float hopper = sdRoundBox(p, float2(-0.15, 0.35), float2(1.05, 0.85), 0.25);
    float grain = 0.8 + 0.2 * frac(sin(dot(floor(p * 5.0), float2(12.9898, 78.233))) * 43758.5453);
    Over(result, float3(0.12, 0.08, 0.05), FillAlpha(hopper, aa));
    Over(result, float3(0.42, 0.27, 0.13) * grain, FillAlpha(hopper + 0.18, aa));

    // Cab with a window, at the rear.
    float cab = sdRoundBox(p, float2(-1.85, -0.75), float2(0.6, 0.55), 0.2);
    Over(result, kOutlineColor, FillAlpha(cab - 0.1, aa));
    Over(result, sand * 0.72, FillAlpha(cab, aa));
    float glass = sdRoundBox(p, float2(-1.6, -0.75), float2(0.25, 0.38), 0.1);
    Over(result, lerp(float3(0.12, 0.22, 0.30), float3(0.55, 0.75, 0.85), saturate(-light.x * 0.5 + 0.5)), FillAlpha(glass, aa));

    // Beacon: steady green while working, orange while stalled.
    float beacon = length(p - float2(-1.85, 0.75)) - 0.3;
    float3 beaconColor = stalled ? float3(1.0, 0.55, 0.10) : float3(0.35, 1.0, 0.45);
    Over(result, kOutlineColor, FillAlpha(beacon - 0.1, aa));
    Over(result, beaconColor, FillAlpha(beacon, aa));
    if (stalled)
        result.rgb += float3(1.0, 0.5, 0.1) * exp(-max(beacon, 0.0) * 3.0) * 0.5;

    return Ghost(result, flags);
}

// Capacitor gauge: a pill with a fill bar; blue when healthy, amber below half, red when nearly empty.
float4 Battery(float2 p, float4 params, float aa)
{
    float fill = saturate(params.x);
    float4 result = float4(0.0, 0.0, 0.0, 0.0);

    static const float2 kHalf = float2(2.0, 0.5);
    float shell = sdRoundBox(p, float2(0.0, 0.0), kHalf, 0.45);
    Over(result, kOutlineColor, FillAlpha(shell - 0.14, aa) * 0.85);
    Over(result, float3(0.08, 0.09, 0.11), FillAlpha(shell, aa) * 0.85);

    float right = -kHalf.x + 0.18 + (kHalf.x - 0.18) * 2.0 * fill;
    float bar = max(sdRoundBox(p, float2(0.0, 0.0), kHalf - 0.18, 0.3), p.x - right);
    float3 color = fill > 0.5 ? kChargeBlue : lerp(float3(1.0, 0.25, 0.15), float3(1.0, 0.72, 0.18), saturate(fill * 2.0 - 0.3));
    Over(result, color, FillAlpha(bar, aa));
    return result;
}

// Turret: a round, bolted base with a domed head and a barrel along its facing (+x), which turns to where it last
// aimed. Its head and barrel glow orange-red while its ground is hot enough to slow its fire (the stalled flag).
float4 Turret(float2 p, float4 params, float aa)
{
    float2 light = LocalLight(params.xy);
    float flags = params.w;
    float hot = MachineFlag(flags, kMachineFlagStalled) ? 1.0 : 0.0;
    float4 result = float4(0.0, 0.0, 0.0, 0.0);

    float radius = length(p);
    float base = radius - 2.6;
    float barrel = sdRoundBox(p, float2(2.0, 0.0), float2(1.5, 0.42), 0.18);
    float2 sp = p + light * 0.6;
    float shadow = min(length(sp) - 2.6, sdRoundBox(sp, float2(2.0, 0.0), float2(1.5, 0.42), 0.18));
    Over(result, float3(0.0, 0.0, 0.0), 0.40 * smoothstep(0.5, -0.3, shadow));

    // The base ring, with eight bolts.
    float bolts = smoothstep(0.75, 0.9, cos(atan2(p.y, p.x) * 8.0)) * smoothstep(0.5, 0.0, abs(radius - 2.1));
    float3 baseColor = lerp(float3(0.24, 0.22, 0.21), float3(0.42, 0.39, 0.35), bolts);
    Over(result, kOutlineColor, FillAlpha(base - 0.14, aa));
    Over(result, baseColor, FillAlpha(base, aa));

    // The barrel, hottest toward its muzzle.
    float heat = hot * smoothstep(0.6, 3.4, p.x);
    float3 steel = lerp(float3(0.30, 0.31, 0.33), float3(1.0, 0.42, 0.12), heat * 0.8);
    Over(result, kOutlineColor, FillAlpha(barrel - 0.14, aa));
    Over(result, steel, FillAlpha(barrel, aa));

    // The domed head, lit from the light, and its sight lamp.
    float head = radius - 1.45;
    float2 n = p / max(radius, 1e-4);
    float lit = 0.8 + 0.35 * dot(n, light) * smoothstep(0.2, 1.45, radius);
    float3 headColor = lerp(float3(0.78, 0.42, 0.16), float3(0.95, 0.28, 0.10), hot) * lit;
    Over(result, kOutlineColor, FillAlpha(head - 0.14, aa));
    Over(result, headColor, FillAlpha(head, aa));
    float lamp = length(p - float2(0.55, 0.0)) - 0.32;
    Over(result, lerp(float3(1.0, 0.86, 0.45), float3(1.0, 0.35, 0.20), hot), FillAlpha(lamp, aa));
    return Ghost(result, flags);
}

// Gatling: a squat armoured mount with a brass drum and a bundle of three thin barrels along its facing (+x), which
// turns to where it last aimed. The barrels' highlight runs across the bundle with the drum angle (params.z), so it
// reads as spinning while the gatling works; the bundle glows orange-red while its ground is hot enough to slow its
// fire (the stalled flag).
float4 Gatling(float2 p, float4 params, float aa)
{
    float2 light = LocalLight(params.xy);
    float flags = params.w;
    float hot = MachineFlag(flags, kMachineFlagStalled) ? 1.0 : 0.0;
    float4 result = float4(0.0, 0.0, 0.0, 0.0);

    float mount = sdRoundBox(p, float2(-0.5, 0.0), float2(2.0, 2.2), 0.7);
    float bundle = sdRoundBox(p, float2(1.9, 0.0), float2(1.6, 0.95), 0.2);
    float2 sp = p + light * 0.6;
    float shadow = min(sdRoundBox(sp, float2(-0.5, 0.0), float2(2.0, 2.2), 0.7),
        sdRoundBox(sp, float2(1.9, 0.0), float2(1.6, 0.95), 0.2));
    Over(result, float3(0.0, 0.0, 0.0), 0.40 * smoothstep(0.5, -0.3, shadow));

    // The mount: dark plate with a lighter rim.
    float rim = smoothstep(0.55, 0.15, abs(mount + 0.35));
    float3 mountColor = lerp(float3(0.22, 0.23, 0.24), float3(0.40, 0.40, 0.38), rim);
    Over(result, kOutlineColor, FillAlpha(mount - 0.14, aa));
    Over(result, mountColor, FillAlpha(mount, aa));

    // The barrel bundle: three barrels side by side, the lit one moving across them as the drum turns.
    float lane = clamp(floor((p.y + 0.95) / 0.6334), 0.0, 2.0);
    float gap = abs(frac((p.y + 0.95) / 0.6334) - 0.5);
    float spin = 0.5 + 0.5 * cos(params.z * 3.0 - lane * 2.094);
    float heat = hot * smoothstep(0.4, 3.4, p.x);
    float3 steel = lerp(float3(0.26, 0.27, 0.29), float3(0.50, 0.52, 0.55), spin);
    steel = lerp(steel, float3(0.10, 0.10, 0.11), smoothstep(0.36, 0.5, gap));
    steel = lerp(steel, float3(1.0, 0.42, 0.12), heat * 0.8);
    Over(result, kOutlineColor, FillAlpha(bundle - 0.14, aa));
    Over(result, steel, FillAlpha(bundle, aa));
    // A collar holds the bundle near the muzzle.
    float collar = sdRoundBox(p, float2(2.7, 0.0), float2(0.22, 1.1), 0.1);
    Over(result, float3(0.16, 0.16, 0.17), FillAlpha(collar, aa));

    // The brass drum the barrels turn in, lit from the light.
    float radius = length(p + float2(0.5, 0.0));
    float drum = radius - 1.35;
    float2 n = (p + float2(0.5, 0.0)) / max(radius, 1e-4);
    float lit = 0.8 + 0.35 * dot(n, light) * smoothstep(0.2, 1.35, radius);
    float3 drumColor = lerp(float3(0.84, 0.70, 0.24), float3(0.95, 0.30, 0.10), hot) * lit;
    Over(result, kOutlineColor, FillAlpha(drum - 0.14, aa));
    Over(result, drumColor, FillAlpha(drum, aa));
    float hub = radius - 0.42;
    Over(result, float3(0.18, 0.17, 0.15), FillAlpha(hub, aa));
    return Ghost(result, flags);
}

#endif // EMBERWARD_ENTITY_MACHINES_HLSLI

// A generator's rectangular casing houses paired heat exchangers and an output gauge.
float4 Generator(float2 p, float4 params, float aa)
{
    float output = saturate(params.y);
    float4 result = float4(0.0, 0.0, 0.0, 0.0);
    float body = sdRoundBox(p, float2(0.0, 0.0), float2(2.9, 2.3), 0.5);
    Over(result, kOutlineColor, FillAlpha(body - 0.26, aa));
    Over(result, float3(0.24, 0.19, 0.31), FillAlpha(body, aa));
    float2 side = float2(abs(p.x) - 1.8, p.y);
    float block = sdRoundBox(side, float2(0.0, 0.0), float2(0.58, 1.7), 0.13);
    float fin = smoothstep(0.14, 0.26, abs(frac(p.y * 1.45) - 0.5));
    float3 metal = lerp(float3(0.37, 0.28, 0.48), float3(0.66, 0.53, 0.78), fin);
    Over(result, metal, FillAlpha(block, aa));
    float gauge = sdRoundBox(p, float2(0.0, 0.0), float2(0.67, 1.55), 0.30);
    Over(result, kOutlineColor, FillAlpha(gauge, aa));
    float glow = smoothstep(0.0, 0.4, output);
    float3 light = lerp(float3(0.36, 0.25, 0.45), float3(0.83, 0.64, 1.0), glow);
    float fill = FillAlpha(gauge + 0.18, aa) * smoothstep(-aa, aa, p.y - (1.35 - 2.70 * output));
    Over(result, light, fill);
    return Ghost(result, params.w);
}
