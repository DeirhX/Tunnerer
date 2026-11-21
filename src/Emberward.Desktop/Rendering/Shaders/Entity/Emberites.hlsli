// ============================================================================
// Entity/Emberites.hlsli — Emberites and a nest's resting embers
// ============================================================================
//
// An emberite is a small wriggling body along its travel direction (+x): bright ember while it carries its heat,
// dull once spent, a dim glow under the ground while it burrows. A nest's embers are a few pulsing dots on the
// deposit, drawn while creatures rest in it; they flare and flicker fast while the nest stirs before a wave. See Layout.hlsli for the params.
// ============================================================================

#ifndef EMBERWARD_ENTITY_EMBERITES_HLSLI
#define EMBERWARD_ENTITY_EMBERITES_HLSLI

#include "Common.hlsli"

float4 Emberite(float2 p, float4 params, float aa)
{
    float4 result = float4(0.0, 0.0, 0.0, 0.0);
    float phase = params.z;
    if (params.w < 0.5)
    {
        float charge = saturate(params.x);
        float burrow = saturate(params.y);
        // The body sways as a wave runs down it, more at the tail.
        float sway = sin(phase * 9.0 + p.x * 3.5) * 0.26 * saturate(0.55 - p.x * 0.5);
        float2 q = float2(p.x, p.y - sway);
        float dist = sdSegment(q, float2(-0.85, 0.0), float2(0.45, 0.0));
        float along = saturate((p.x + 1.2) / 1.9);                 // 0 at the tail, 1 at the head
        float radius = lerp(0.16, 0.38, along);

        float body = FillAlpha(dist - radius, aa) * lerp(1.0, 0.25, burrow);
        float3 dull = float3(0.42, 0.20, 0.12);
        float3 hot = lerp(float3(1.0, 0.52, 0.16), float3(1.0, 0.90, 0.55), along);
        float3 skin = lerp(dull, hot, charge);
        float halo = exp(-max(dist - radius * 0.5, 0.0) * 2.6) * (0.15 + 0.85 * charge);
        float3 glow = float3(1.0, 0.50, 0.14) * halo * lerp(0.75, 0.45, burrow);
        result = float4(skin * body + glow, body);
    }
    else
    {
        float reach = max(params.y, 0.5);
        float resting = saturate(params.x);
        float stir = saturate(params.x - 1.0);
        float3 sum = float3(0.0, 0.0, 0.0);
        for (int i = 0; i < 5; i++)
        {
            float fi = float(i);
            float angle = fi * 2.399 + 0.6;
            float2 at = float2(cos(angle), sin(angle)) * reach * (0.18 + 0.15 * fi);
            float2 d = p - at;
            float pulse = 0.55 + 0.45 * sin(phase * lerp(2.3, 19.0, stir) + fi * 1.7);
            sum += float3(1.0, 0.52, 0.18) * exp(-dot(d, d) * lerp(5.0, 1.6, stir)) * pulse;
        }
        // Stirring: the embers swell, and a hot glow rises over the whole deposit.
        float swell = exp(-dot(p, p) / (reach * reach) * 2.2) * (0.6 + 0.4 * sin(phase * 19.0));
        sum += float3(1.0, 0.78, 0.40) * swell * stir * 0.7;
        result = float4(sum * resting * lerp(0.9, 1.5, stir), 0.0);
    }
    return result;
}

#endif // EMBERWARD_ENTITY_EMBERITES_HLSLI
