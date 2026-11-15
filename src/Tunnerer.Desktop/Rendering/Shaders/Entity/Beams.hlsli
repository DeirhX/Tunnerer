// ============================================================================
// Entity/Beams.hlsli — Link, charge, preview and tether beams
// ============================================================================
//
// Beams along +x from x = 0 to params.x (their length in cells). Each flows
// toward +x. The shader owns the beams' colours.
// ============================================================================

#ifndef TUNNERER_ENTITY_BEAMS_HLSLI
#define TUNNERER_ENTITY_BEAMS_HLSLI

#include "Common.hlsli"

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

#endif // TUNNERER_ENTITY_BEAMS_HLSLI
