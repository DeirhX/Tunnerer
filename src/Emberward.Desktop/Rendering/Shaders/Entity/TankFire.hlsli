// ============================================================================
// Entity/TankFire.hlsli — The fire on a tank with a damaged hull
// ============================================================================
//
// Soot over the hull, flame tongues that grow with the damage, and smoke that rises and leans. Everything is drawn in
// the tank's local frame (cells, +y down, so a flame rises toward -y). Params are documented in Layout.hlsli (kind 23).
// ============================================================================

#ifndef EMBERWARD_ENTITY_TANKFIRE_HLSLI
#define EMBERWARD_ENTITY_TANKFIRE_HLSLI

static const float3 kFireCore = float3(1.00, 0.88, 0.45);
static const float3 kFireOuter = float3(1.00, 0.32, 0.06);

float FireNoise(float2 c)
{
    return frac(sin(dot(c, float2(41.7, 289.3))) * 24634.6345);
}

// One flame tongue standing at x = `baseX` on the hull, `height` cells tall: 1 inside its body, falling off to its tip.
// `v` runs 0 at its foot to 1 at its tip.
float FlameTongue(float2 p, float baseX, float height, float sway, out float v)
{
    float rise = (1.5 - p.y) / max(height, 0.01);
    v = saturate(rise);
    float centre = baseX + sway * rise * rise;
    float halfWidth = lerp(1.25, 0.05, saturate(rise)) ;
    float body = smoothstep(halfWidth, halfWidth * 0.35, abs(p.x - centre));
    return body * step(0.0, rise) * step(rise, 1.0);
}

float4 TankFire(float2 p, float3 team, float4 params, float aa)
{
    float level = saturate(params.x);
    float t = params.y;                      // seconds into a 4 s cycle: every frequency below is a multiple of 1/4 Hz
    float2 lean = params.zw;                 // the way smoke drifts, in cells over its rise

    float4 result = float4(0.0, 0.0, 0.0, 0.0);

    // Soot: the hull goes dark and blistered as the fire grows.
    float hull = FillAlpha(sdRoundBox(p, float2(0.0, 0.0), float2(4.4, 4.4), 1.2), aa);
    float blister = 0.55 + 0.45 * FireNoise(floor(p * 1.7));
    Over(result, float3(0.03, 0.02, 0.02), hull * level * 0.45 * blister);

    // Smoke: dark puffs that rise from the hull and drift along the lean, thinner as they climb.
    float puffPhase = frac(t * 0.5 + FireNoise(floor(p * 0.9 + float2(0.0, 3.0))) );
    float smokeCentre = (p.x - lean.x * (-p.y)) ;
    float smoke = smoothstep(2.6, 0.0, abs(smokeCentre)) * step(p.y, 0.0) * smoothstep(-11.0, -2.0, p.y)
        * (0.4 + 0.6 * puffPhase) * level * level;
    Over(result, float3(0.08, 0.07, 0.07), saturate(smoke * 0.5));

    // Flames: up to three tongues, the second and third lighting as the hull falls further.
    float flick = 0.75 + 0.25 * sin(t * 18.85) * sin(t * 12.57 + 1.3);
    float v0; float v1; float v2;
    float f0 = FlameTongue(p, 0.0, (3.0 + 6.0 * level) * flick, lean.x * 2.0 + 0.6 * sin(t * 6.28), v0);
    float f1 = FlameTongue(p, -2.6, (2.0 + 5.0 * level) * (0.8 + 0.2 * sin(t * 14.14 + 2.0)), lean.x * 2.0 - 0.5 * sin(t * 9.42), v1)
        * smoothstep(0.25, 0.45, level);
    float f2 = FlameTongue(p, 2.6, (2.0 + 5.0 * level) * (0.8 + 0.2 * sin(t * 10.9956 + 4.0)), lean.x * 2.0 + 0.5 * sin(t * 7.85 + 1.0), v2)
        * smoothstep(0.55, 0.75, level);
    float3 flame = lerp(kFireCore, kFireOuter, v0) * f0
        + lerp(kFireCore, kFireOuter, v1) * f1 + lerp(kFireCore, kFireOuter, v2) * f2;
    // Additive: fire gives light, not a body; a small ember glow sits on the hull itself.
    float ember = hull * level * (0.25 + 0.15 * sin(t * 15.7 + FireNoise(floor(p * 2.0)) * 6.28));
    result.rgb += flame * 1.6 * step(0.001, level) + kFireOuter * ember;
    return result;
}

#endif // EMBERWARD_ENTITY_TANKFIRE_HLSLI
