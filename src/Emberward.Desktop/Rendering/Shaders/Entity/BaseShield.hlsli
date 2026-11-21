// ============================================================================
// Entity/BaseShield.hlsli — A base's shield shell, its armor and shield bars, and the flash of an enemy hit
// ============================================================================
//
// The shell follows the base's shield: bright and steady when it is strong, thin and flickering red when weak, absent
// when the base is dark (only sparks run along its wall). Bars above the base show armor and shield while it is hurt.
// Hit flashes are small quads at the cell a bullet struck: a ring that spreads and fades, cool when the shield took the
// hit, hot when it was weak. Params are documented in Layout.hlsli (kinds 20 and 21).
// ============================================================================

#ifndef EMBERWARD_ENTITY_BASESHIELD_HLSLI
#define EMBERWARD_ENTITY_BASESHIELD_HLSLI

#include "ShieldVeins.hlsli"

// Half the base's side in cells (Tweaks.Base.BaseSize / 2) and the gap between its wall and the shell.
static const float kBaseHalf = 17.5;
static const float kShellMargin = 2.5;
static const float3 kShieldCool = float3(0.45, 0.85, 1.00);
static const float3 kShieldHot = float3(1.00, 0.35, 0.18);

float ShieldHash(float2 c)
{
    return frac(sin(dot(c, float2(12.9898, 78.233))) * 43758.5453);
}

// A flat bar `halfWidth` cells either side of `center`, filled from the left to `fill`, over a dark back.
float4 ShieldBar(float2 p, float2 center, float halfWidth, float fill, float3 color, float aa)
{
    float back = FillAlpha(sdRoundBox(p, center, float2(halfWidth + 0.35, 0.85), 0.4), aa);
    float x = p.x - (center.x - halfWidth);
    float filled = FillAlpha(sdRoundBox(p, center, float2(halfWidth, 0.5), 0.25), aa) * step(x, 2.0 * halfWidth * fill);
    float4 result = float4(0.0, 0.0, 0.0, 0.0);
    Over(result, float3(0.02, 0.02, 0.03), back * 0.7);
    Over(result, color, filled);
    return result;
}

float4 BaseShield(float2 p, float3 team, float4 params, float aa)
{
    float strength = params.x;
    float armor = params.y;
    float glow = params.z;
    float flags = floor(params.w);
    float t = frac(params.w) / 0.999 * 4.0;     // seconds into a 4 s cycle
    float bars = floor(flags * 0.25);
    float dark = floor(flags * 0.5) - 2.0 * bars;
    float shell = flags - 2.0 * floor(flags * 0.5);

    float4 result = float4(0.0, 0.0, 0.0, 0.0);

    // The shell: a rounded square a little outside the wall.
    float d = sdRoundBox(p, float2(0.0, 0.0), float2(kBaseHalf + kShellMargin, kBaseHalf + kShellMargin), 7.0);
    float live = shell * (1.0 - dark);
    float weak = 1.0 - saturate(strength * 3.0);
    float flicker = lerp(1.0, 0.55 + 0.45 * sin(t * 9.42478 + 1.7 * ShieldHash(floor(p * 0.25))), weak);
    float rim = exp(-d * d * 0.9);
    float edge = saturate(1.0 + d * 0.25) * step(d, 0.0);
    float weave = 0.5 + 0.5 * sin((p.x + p.y) * 1.3 - t * 3.14159);
    float body = (0.012 + 0.07 * weave) * edge;
    float3 tint = lerp(kShieldHot, lerp(kShieldCool, team, 0.25), saturate(strength * 2.0));
    float shellAlpha = live * flicker * ((0.10 + 0.55 * strength) * rim + body * (0.3 + strength));
    float3 shellColor = lerp(tint, float3(1.0, 1.0, 1.0), glow * 0.6);
    // Additive: a hit lights the whole shell for a moment.
    result.rgb += shellColor * (shellAlpha + live * glow * 0.45 * rim);
    // A gate hit lights the veins of the whole shell for a moment, not only its rim.
    float veinBand = step(d, 0.0) * saturate(1.0 + (d + 7.0) * 0.5);
    result.rgb += tint * live * glow * veinBand * VeinLines(p * 0.3, 0.09) * 1.4;

    // A dark base has no shell: sparks crawl along its wall instead.
    float wall = abs(sdRoundBox(p, float2(0.0, 0.0), float2(kBaseHalf, kBaseHalf), 0.0));
    float spark = step(0.965, ShieldHash(floor(p * 1.5) + floor(t * 10.0))) * step(wall, 1.2);
    result.rgb += dark * spark * float3(1.0, 0.7, 0.35);

    // Bars above the base: armor, then shield.
    float barY = -(kBaseHalf + kShellMargin + 2.2);
    float3 armorColor = lerp(float3(0.95, 0.2, 0.15), float3(0.35, 0.95, 0.4), saturate(armor * 1.6 - 0.3));
    float4 armorBar = ShieldBar(p, float2(0.0, barY), 10.0, armor, armorColor, aa);
    float4 shieldBar = ShieldBar(p, float2(0.0, barY + 2.0), 10.0, strength, kShieldCool, aa);
    float armorShown = bars;
    float shieldShown = bars * shell;
    Over(result, armorBar.rgb / max(armorBar.a, 1e-4), armorBar.a * armorShown);
    Over(result, shieldBar.rgb / max(shieldBar.a, 1e-4), shieldBar.a * shieldShown);
    return result;
}

float4 ShieldHit(float2 p, float3 team, float4 params, float aa)
{
    float age = params.x;
    float strength = params.y;
    float destroyed = params.z;
    float fade = 1.0 - age;
    float r = length(p);
    float radius = lerp(1.2, 7.5, 1.0 - fade * fade);
    float width = 0.7 + age * 1.1;
    float ringOffset = (r - radius) / width;
    float ring = exp(-ringOffset * ringOffset) * fade;
    float core = exp(-r * r * 0.6) * fade * fade;
    float3 color = lerp(kShieldHot, lerp(kShieldCool, team, 0.25), strength);
    color = lerp(color, float3(1.0, 0.9, 0.7), destroyed);
    float power = 1.0 + destroyed;
    float3 add = color * (ring * 0.9 + core * 1.3) * power + float3(1.0, 1.0, 1.0) * core * 0.4;
    // The patch that took the shot is red, whatever the shield's strength.
    float redRadius = lerp(2.2, 5.0, age);
    add += float3(1.0, 0.10, 0.06) * exp(-r * r / (redRadius * redRadius)) * sqrt(fade) * 1.2;
    return float4(add, 0.0);
}

#endif // EMBERWARD_ENTITY_BASESHIELD_HLSLI
