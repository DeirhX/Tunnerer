// ============================================================================
// ScreenFxPs.hlsl — Whole-screen effects: hit, overheat, freeze
// ============================================================================
//
// Runs on the finished game image (terrain, post pass and entities), before
// the HUD, so the shake and distortion move tanks and shots with the ground.
// The renderer runs it only while an effect shows (ScreenFx.w); otherwise the
// game image is shown as it is. Levels come from Rendering/ScreenEffects.cs.
//
//   Hit      — the view jolts away from the shooter and rings back, colours
//              split, a brief bright punch and a red shade on the side the
//              shot came from.
//   Overheat — heat haze that ripples the whole view (strongest at the edges),
//              a warm grade and a flickering burning rim.
//   Freeze   — a cold, drained grade and frost growing in from the edges and
//              corners, refracting what is under it, with glints.
//
// Inputs:
//   sceneTex (t0) — the game image (stored bottom row first where ScreenFx.w
//                   is 2: the browser's WebGL framebuffer texture)
// ============================================================================

#include "PostPs/Common/Registers.hlsli"
#include "PostPs/Common/PostParams.hlsli"
#include "PostPs/Common/MathNoise.hlsli"

Texture2D sceneTex : FRAGMENT_TEXTURE(0);
SamplerState s0 : FRAGMENT_SAMPLER(0);

static const float3 kLuma = float3(0.299, 0.587, 0.114);

float3 SampleScene(float2 uv)
{
    uv = clamp(uv, TexelSize * 0.5, 1.0 - TexelSize * 0.5);
    // In the browser the game image is a WebGL framebuffer texture, kept bottom row first; the page says so with 2.
    if (ScreenFx.w > 1.5)
        uv.y = 1.0 - uv.y;
    return sceneTex.Sample(s0, uv).rgb;
}

float3 ScreenEffects(float2 uv)
{
    float hit = ScreenFx.x;
    float overheat = ScreenFx.y;
    float freeze = ScreenFx.z;
    float2 shake = ScreenFxMotion.xy;
    float2 hitFrom = ScreenFxMotion.zw;

    // Lengths are given at 1080 pixels high and scaled to the view.
    float sizeScale = ViewSize.y / 1080.0;
    float2 px = uv * ViewSize;
    // Centred, aspect-correct coordinates in view heights; 0 in the middle.
    float2 c = (uv - 0.5) * float2(ViewSize.x / ViewSize.y, 1.0);
    float maxRadius = length(float2(ViewSize.x / ViewSize.y, 1.0) * 0.5);
    float radial = length(c) / maxRadius;             // 0 centre .. 1 corner
    // Distance to the nearest edge, in view heights.
    float edgeDistance = min(min(px.x, ViewSize.x - px.x), min(px.y, ViewSize.y - px.y)) / ViewSize.y;

    float2 sampleUv = uv;

    // ---- Hit: punch in and shake ----------------------------------------------
    if (hit > 0.0)
    {
        // The shake is given at 1080 pixels high. A slight zoom hides the clamped edge it would otherwise show:
        // 3% leaves 16 such pixels on each side at the hit, more than the shake ever throws (about 15).
        sampleUv = 0.5 + (sampleUv - 0.5) * (1.0 - 0.03 * hit);
        sampleUv -= shake * sizeScale * TexelSize;
    }

    // ---- Overheat: heat haze ---------------------------------------------------
    if (overheat > 0.0)
    {
        float t = Time;
        float2 q = px / sizeScale;
        float wobbleA = valueNoise(q * 0.010 + float2(0.0, t * 1.6)) * 6.2832;
        float wobbleB = valueNoise(q * 0.013 - float2(t * 0.8, 0.0)) * 6.2832;
        float waveX = sin(q.y * 0.055 + t * 9.0 + wobbleA);
        float waveY = sin(q.x * 0.041 - t * 6.5 + wobbleB);
        // Strongest at the edges at first; as the heat climbs the haze takes the whole view.
        float spread = saturate(radial * radial + overheat * overheat);
        float amplitude = (1.0 + 3.6 * spread) * overheat * sizeScale;
        sampleUv += float2(waveX, waveY * 0.7) * amplitude * TexelSize;
    }

    // ---- Freeze: frost mask and its refraction ----------------------------------
    float frost = 0.0;
    float frostRim = 0.0;
    float frostGrain = 0.0;
    // How far in the frost reaches (view heights): a thin edge at first, more at the corners, closing in as the cold
    // deepens until at 1 it has covered the middle (0.5 from every edge). The noise below moves the front by at most
    // 0.10, and the veins and rim show up to 0.01 beyond it, so pixels further in than that skip it all.
    float reach = 0.02 + 0.72 * pow(freeze, 1.7) + 0.08 * radial * radial * freeze;
    if (freeze > 0.0 && edgeDistance < reach + 0.11)
    {
        float2 q = px / (ViewSize.y * 0.07);
        float grain = fbmNoise(q, 4);
        // Large lobes make the front ragged; ridged noise draws the veins of the crystals.
        float lobes = valueNoise(px / (ViewSize.y * 0.22));
        float ridge = 1.0 - abs(2.0 * valueNoise(q * 2.3 + 7.1) - 1.0);
        float veins = ridge * ridge * ridge * ridge;
        frostGrain = grain;

        // The noise makes the front ragged.
        float front = reach - edgeDistance + ((lobes - 0.5) * 0.14 + (grain - 0.5) * 0.06) * saturate(freeze * 3.0);
        // Thin at the front, thick at the edge of the screen.
        frost = smoothstep(0.0, 0.03, front) * (0.55 + 0.45 * saturate(front / 0.08)) + veins * smoothstep(-0.01, 0.02, front) * 0.35;
        frost = saturate(frost);
        frostRim = saturate(1.0 - abs(front) / 0.010);

        // Frost bends the light that passes through it.
        if (frost > 0.0)
        {
            float gx = fbmNoise(q + float2(0.37, 0.0), 2) - fbmNoise(q - float2(0.37, 0.0), 2);
            float gy = fbmNoise(q + float2(0.0, 0.37), 2) - fbmNoise(q - float2(0.0, 0.37), 2);
            sampleUv += float2(gx, gy) * frost * 9.0 * sizeScale * TexelSize;
        }
    }

    // ---- Sample, with a colour split on a hit ------------------------------------
    float3 color;
    if (hit > 0.0)
    {
        // Split along the line from the centre, nudged along the hit, widest at the edges.
        float2 splitDir = normalize(c + hitFrom * 0.25 + 1e-4);
        float2 split = splitDir * hit * (2.0 + 9.0 * radial) * sizeScale * TexelSize;
        color.r = SampleScene(sampleUv + split).r;
        color.g = SampleScene(sampleUv).g;
        color.b = SampleScene(sampleUv - split).b;
    }
    else
    {
        color = SampleScene(sampleUv);
    }

    // ---- Hit: punch and a red shade toward the shooter -------------------------
    if (hit > 0.0)
    {
        float luma = dot(color, kLuma);
        color = lerp(color, luma.xxx, 0.35 * hit);
        color += float3(1.0, 0.86, 0.80) * (0.16 * hit * hit * hit);
        float2 outward = normalize(c + 1e-4);
        float facing = saturate(dot(outward, hitFrom));
        float side = facing * facing * smoothstep(0.25, 1.0, radial);
        color = lerp(color, float3(0.85, 0.06, 0.03), side * hit * 0.45);
    }

    // ---- Overheat: warm grade and burning rim ------------------------------------
    if (overheat > 0.0)
    {
        float t = Time;
        color = lerp(color, color * float3(1.20, 0.90, 0.68), 0.6 * overheat);
        float2 q = px / (ViewSize.y * 0.09);
        // Flames lick upward: the noise scrolls up the screen.
        float flame = fbmNoise(float2(q.x, q.y + t * 1.8), 3);
        float pulse = 0.78 + 0.22 * sin(t * 7.0);
        // The burning rim closes in as the heat climbs; at 1 it has taken the whole view.
        float inner = lerp(0.62, -0.45, pow(overheat, 1.2));
        float rim = smoothstep(inner, inner + 0.38, radial + (flame - 0.5) * 0.35);
        // A thin rim burns brightly from the start, so crossing the safe maximum is seen at once.
        float glow = rim * pulse * (0.5 + 0.5 * overheat);
        color = lerp(color, float3(0.95, 0.28, 0.05), glow * (0.30 + 0.15 * radial));
        color += float3(1.0, 0.45, 0.10) * glow * flame * 0.35;
    }

    // ---- Freeze: cold grade, frost and glints ------------------------------------
    if (freeze > 0.0)
    {
        float luma = dot(color, kLuma);
        float3 cold = luma * float3(0.78, 0.92, 1.12) + float3(0.0, 0.01, 0.04);
        color = lerp(color, cold, 0.55 * freeze);

        float3 frostColor = float3(0.70, 0.83, 0.96) * (0.55 + 0.55 * frostGrain);
        // A little thinner in the middle, so the tank still shows through a full freeze.
        color = lerp(color, frostColor, frost * (0.58 + 0.20 * radial));
        color += float3(0.75, 0.90, 1.00) * frostRim * 0.30;

        // Glints: a few frost cells catch the light now and then.
        float2 cell = floor(px / (6.0 * max(sizeScale, 0.5)));
        float seed = hash21(cell);
        float twinkle = saturate(sin(Time * (2.0 + 3.0 * seed) + seed * 40.0) * 4.0 - 3.0);
        color += step(0.992, seed) * twinkle * frost * 0.6;
    }

    return color;
}

float4 main(float4 pos : SV_POSITION, float2 uv : TEXCOORD0) : SV_Target
{
    float3 color;
    if (abs(PostParamsLayoutVersion - kPostParamsLayoutVersion) > 0.001)
        color = SampleScene(uv);
    else
        color = ScreenEffects(uv);
    return float4(color, 1.0);
}
