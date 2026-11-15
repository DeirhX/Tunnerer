// ============================================================================
// Entity/Machines.hlsli — Relay, harvester, pylon and battery gauge
// ============================================================================
//
// Machine bodies. A carried machine is drawn as a see-through ghost. The shader
// owns the machines' colours.
// ============================================================================

#ifndef TUNNERER_ENTITY_MACHINES_HLSLI
#define TUNNERER_ENTITY_MACHINES_HLSLI

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

#endif // TUNNERER_ENTITY_MACHINES_HLSLI
