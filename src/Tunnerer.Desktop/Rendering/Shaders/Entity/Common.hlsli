// ============================================================================
// Entity/Common.hlsli — Shared entity shading helpers
// ============================================================================
//
// Lighting, outline colours, signed-distance primitives and premultiplied
// compositing shared by every entity shape. Distances and `aa` are in world cells.
// ============================================================================

#ifndef TUNNERER_ENTITY_COMMON_HLSLI
#define TUNNERER_ENTITY_COMMON_HLSLI

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

// Anti-aliased alpha of the inside of a signed distance (negative inside; aa = one pixel in cells).
float FillAlpha(float dist, float aa)
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

// Charge (pylons, charge beams, battery gauges) is blue.
static const float3 kChargeBlue = float3(0.30, 0.55, 1.00);
static const float3 kChargeViolet = float3(0.62, 0.45, 1.00);

#endif // TUNNERER_ENTITY_COMMON_HLSLI
