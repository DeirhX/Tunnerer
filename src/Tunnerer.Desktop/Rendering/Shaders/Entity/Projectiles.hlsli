// ============================================================================
// Entity/Projectiles.hlsli — Shots and foam blobs
// ============================================================================
//
// Projectiles, drawn along their direction of travel (+x).
// ============================================================================

#ifndef TUNNERER_ENTITY_PROJECTILES_HLSLI
#define TUNNERER_ENTITY_PROJECTILES_HLSLI

#include "Common.hlsli"

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

#endif // TUNNERER_ENTITY_PROJECTILES_HLSLI
