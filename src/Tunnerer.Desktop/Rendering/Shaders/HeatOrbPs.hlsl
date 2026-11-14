// ============================================================================
// HeatOrbPs.hlsl — The HUD temperature orb (SDL_GPU backend)
// ============================================================================
//
// Drawn by the ImGui renderer in place of a texture when the HUD draws an image
// with HeatOrbEffect.TextureId; ImGuiVs supplies the quad, uv 0..1 across it.
// The orb is a glass sphere always full of liquid. Its colour comes from the
// temperature, and its state follows it:
//   frozen  ice: pale, still, cracked, frost creeping in from the glass, glints
//   calm    slow swirling currents and caustics around ambient
//   boil    rising bubbles, faster churn and a glowing core as it heats
//   burning past the safe maximum: rolling boil, pulsing white-hot core
// ============================================================================

#include "PostPs/Common/Registers.hlsli"

cbuffer HeatOrbParams : FRAGMENT_UNIFORMS(0)
{
    float4 LiquidColor;   // .rgb liquid colour for the temperature
    float4 State;         // .x frozen 0..1, .y boil 0..1, .z burning 0..1 (pulsing), .w time in seconds
};

float Hash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float2 Hash22(float2 p)
{
    float n = Hash21(p);
    return float2(n, Hash21(p + n + 17.17));
}

float ValueNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    float a = Hash21(i);
    float b = Hash21(i + float2(1, 0));
    float c = Hash21(i + float2(0, 1));
    float d = Hash21(i + float2(1, 1));
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

float Fbm(float2 p)
{
    float sum = 0.0;
    float amp = 0.5;
    [unroll] for (int i = 0; i < 4; i++)
    {
        sum += amp * ValueNoise(p);
        p = p * 2.03 + float2(1.7, 9.2);
        amp *= 0.5;
    }
    return sum;
}

// Distance to the nearest Voronoi edge (small along cracks between ice cells).
float VoronoiEdge(float2 p)
{
    float2 cell = floor(p);
    float2 f = frac(p);
    float d1 = 8.0, d2 = 8.0;
    [unroll] for (int y = -1; y <= 1; y++)
    {
        [unroll] for (int x = -1; x <= 1; x++)
        {
            float2 o = float2(x, y);
            float2 r = o + Hash22(cell + o) - f;
            float d = dot(r, r);
            if (d < d1) { d2 = d1; d1 = d; }
            else if (d < d2) { d2 = d; }
        }
    }
    return sqrt(d2) - sqrt(d1);
}

// Bubbles rising through one layer of cells; returns ring brightness.
float BubbleLayer(float2 p, float scale, float speed, float density, float time)
{
    float2 q = p * scale;
    q.y += time * speed;
    float2 cell = floor(q);
    float2 f = frac(q) - 0.5;
    float h = Hash21(cell + scale);
    if (h > density)
        return 0.0;
    float2 c = (Hash22(cell * 1.31 + scale) - 0.5) * 0.5;
    c.x += sin(time * 3.0 + h * 40.0) * 0.08;
    float radius = 0.16 + 0.16 * Hash21(cell + 7.0);
    float d = length(f - c);
    float edge = 0.045;
    float ring = smoothstep(radius, radius - edge, d) - smoothstep(radius - edge, radius - 2.5 * edge, d);
    float body = smoothstep(radius, radius - edge, d) * 0.18;
    float glint = smoothstep(radius * 0.28, 0.0, length(f - c + radius * float2(0.38, 0.42)));
    return ring * 0.9 + body + glint;
}

float4 main(float4 pos : SV_POSITION, float4 col : COLOR0, float2 uv : TEXCOORD0) : SV_Target
{
    float2 p = uv * 2.0 - 1.0;
    float d = length(p);
    float aa = max(fwidth(d) * 1.5, 1e-4);
    float mask = smoothstep(1.0, 1.0 - aa, d);
    if (mask <= 0.0)
        return float4(0, 0, 0, 0);

    float frozen = saturate(State.x);
    float boil = saturate(State.y);
    float burning = saturate(State.z);
    float time = State.w;
    float liquidTime = time * (1.0 - 0.95 * frozen);   // ice barely moves

    float3 n = float3(p, sqrt(saturate(1.0 - d * d)));
    float3 baseColor = LiquidColor.rgb;

    // Refraction: look through the sphere, so the inside bulges toward the middle.
    float2 inner = p * (0.75 + 0.25 * n.z);

    // Currents: domain-warped noise that churns faster as the liquid heats.
    float churn = 0.15 + 0.6 * boil + 0.6 * burning;
    float2 flow = inner * 2.2 + float2(0.0, liquidTime * churn);
    float2 warp = float2(Fbm(flow + liquidTime * 0.1), Fbm(flow + 5.2 - liquidTime * 0.12));
    float currents = Fbm(flow + warp * (1.2 + boil));
    float caustic = pow(saturate(1.0 - abs(currents * 2.0 - 1.0)), 6.0);

    // Thickness: the glass edge looks through more liquid, so it is darker; the middle glows.
    float thickness = lerp(0.40, 1.05, n.z);
    float3 color = baseColor * thickness * (0.80 + 0.35 * currents);
    color += baseColor * caustic * (0.25 + 0.35 * boil);

    // Heat glow from the core, then a white-hot pulsing heart when burning.
    float core = saturate(1.0 - d * 1.15);
    color += baseColor * core * core * (0.6 * boil + 0.8 * burning);
    color += float3(1.0, 0.85, 0.55) * pow(core, 3.0) * burning * (0.5 + 0.5 * sin(time * 9.0));

    // Bubbles: a few slow ones when warm, a rolling boil past the safe maximum.
    float bubbleAmount = saturate(boil * 1.2 + burning);
    if (bubbleAmount > 0.0)
    {
        float density = 0.15 + 0.6 * bubbleAmount;
        float speed = 0.6 + 1.6 * bubbleAmount;
        float b = BubbleLayer(inner, 2.6, speed, density, time)
                + BubbleLayer(inner + 0.37, 4.0, speed * 1.4, density, time) * 0.8
                + BubbleLayer(inner + 0.71, 6.0, speed * 1.9, density * 0.8, time) * 0.6;
        color += lerp(baseColor, float3(1, 1, 1), 0.6) * b * bubbleAmount * 0.8;
    }

    // Ice: desaturate toward pale blue-white, crack it, frost it in from the glass and let it glint.
    if (frozen > 0.0)
    {
        float3 ice = lerp(baseColor, float3(0.80, 0.92, 1.0), 0.55) * (0.65 + 0.45 * n.z);
        float cracks = smoothstep(0.06, 0.0, VoronoiEdge(inner * 4.0 + 3.1))
                     + 0.5 * smoothstep(0.04, 0.0, VoronoiEdge(inner * 9.0 + 11.7));
        float frost = smoothstep(0.45, 1.0, d + (Fbm(p * 7.0) - 0.5) * 0.5);
        float2 sparkleCell = floor(p * 26.0);
        float sparkle = step(0.965, Hash21(sparkleCell)) *
                        pow(0.5 + 0.5 * sin(time * 3.0 + Hash21(sparkleCell + 3.0) * 30.0), 8.0);
        ice += float3(0.85, 0.95, 1.0) * (cracks * 0.45 + frost * 0.65) + sparkle * 0.9;
        color = lerp(color, ice, frozen);
    }

    // Glass: fresnel rim, a soft key highlight and a sharp specular glint.
    float fresnel = pow(1.0 - n.z, 3.0);
    color += lerp(baseColor, float3(1, 1, 1), 0.5) * fresnel * 0.45;
    float3 lightDir = normalize(float3(-0.45, -0.55, 0.70));
    float spec = pow(saturate(dot(reflect(-lightDir, n), float3(0, 0, 1))), 48.0);
    float crescent = smoothstep(0.30, 0.0, length(p - float2(-0.38, -0.45)) - 0.12) * 0.35;
    color += float3(1, 1, 1) * (spec * 0.9 + crescent);

    // A thin dark lip where the glass meets the frame.
    color *= lerp(1.0, 0.35, smoothstep(0.88, 1.0, d));

    return float4(saturate(color), mask * col.a);
}
