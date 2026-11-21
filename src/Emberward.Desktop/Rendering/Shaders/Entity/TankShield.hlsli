// ============================================================================
// Entity/TankShield.hlsli — A tank's shield, shown for a moment when it is hit
// ============================================================================
//
// A bubble around the tank. Nothing is drawn until a hit; then the whole bubble's veins light up with a wavefront from
// the impact, and a red patch glows where the shot landed. Params are documented in Layout.hlsli (kind 22).
// ============================================================================

#ifndef EMBERWARD_ENTITY_TANKSHIELD_HLSLI
#define EMBERWARD_ENTITY_TANKSHIELD_HLSLI

#include "ShieldVeins.hlsli"

static const float kTankShieldRadius = 6.6;
static const float kTankShieldVeinScale = 0.42;
static const float3 kTankShieldCool = float3(0.45, 0.85, 1.00);
static const float3 kTankShieldHot = float3(1.00, 0.35, 0.18);
static const float3 kTankShieldRed = float3(1.00, 0.10, 0.06);

float4 TankShield(float2 p, float3 team, float4 params, float aa)
{
    float fill = params.x;
    float age = saturate(params.y);
    float2 hit = params.zw;
    // The impact cell is on the hull, a few cells inside the bubble: show the patch on the bubble where the shot came in.
    float hitLength = length(hit);
    hit = hit * (hitLength > 0.01 ? (kTankShieldRadius - 1.2) / hitLength : 1.0);
    float fade = 1.0 - age;
    float r = length(p);

    float inside = 1.0 - smoothstep(kTankShieldRadius - 0.7, kTankShieldRadius, r);
    float rim = exp(-(r - kTankShieldRadius + 0.3) * (r - kTankShieldRadius + 0.3) * 1.6);
    float veins = VeinLines(p * kTankShieldVeinScale, 0.09);
    float front = VeinFront(p, hit, age, 15.0, 2.6);
    float light = (0.22 + 1.1 * front) * fade;

    float3 tint = lerp(kTankShieldHot, lerp(kTankShieldCool, team, 0.25), saturate(fill * 2.0));
    float3 add = tint * (veins * light * 1.7 + rim * fade * 0.8 + inside * fade * 0.06);

    // Where the shot landed: a red patch that widens and fades. No hit point (0, 0) means no patch.
    float2 toHit = p - hit;
    float spotRadius = lerp(1.6, 4.2, age);
    float hasHit = step(0.01, length(hit));
    float spot = exp(-dot(toHit, toHit) / (spotRadius * spotRadius)) * sqrt(fade) * hasHit * inside;
    add += kTankShieldRed * spot * 1.4;
    add += float3(1.0, 1.0, 1.0) * spot * spot * 0.35;

    // Additive, like the base shield's flashes: colour with no alpha.
    return float4(add * inside + tint * rim * fade * 0.3 * (1.0 - inside), 0.0);
}

#endif // EMBERWARD_ENTITY_TANKSHIELD_HLSLI
