// ============================================================================
// Entity/Overlays.hlsli — Ground areas: sense ring, reach area, coverage
// ============================================================================
//
// Faint fields drawn on the ground around a machine, the carried machine or a
// network node, under machines and tanks.
// ============================================================================

#ifndef TUNNERER_ENTITY_OVERLAYS_HLSLI
#define TUNNERER_ENTITY_OVERLAYS_HLSLI

#include "Common.hlsli"

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
    float alpha = FillAlpha(ring, aa) * dash * (tooHot ? 0.75 + 0.25 * sin(phase * 6.0) : 0.7);
    float4 result = float4(0.0, 0.0, 0.0, 0.0);
    Over(result, color, alpha);
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
float4 NetworkCoverage(float2 p, float3 tint, float4 params, float aa)
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

#endif // TUNNERER_ENTITY_OVERLAYS_HLSLI
