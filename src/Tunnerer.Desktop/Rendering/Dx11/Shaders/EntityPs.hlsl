// ============================================================================
// EntityPs.hlsl — Entity layer pixel shader (tanks and projectiles)
// ============================================================================
//
// Draws entities procedurally at screen resolution, over the finished terrain
// frame. Each entity is a quad whose `local` coordinate is in world cells with
// +x along the entity's facing (tank hull, turret) or travel (projectile)
// direction, so shapes are signed-distance functions of `local` and stay sharp
// at any pixel scale.
//
// Output is premultiplied alpha (blend: One, InvSrcAlpha), so opaque bodies and
// additive glows share one pass: a glow adds colour with zero alpha.
//
// Shape kinds (color.a) and their params:
//   0 tank hull   — .xy = facing (cos, sin), .z = tread travel (cells),
//                   .w = state (0 normal, 1 hit flash, 2 spawn protection)
//   1 tank turret — .xy = barrel direction (cos, sin), .w = state as above
//   2 hot shot    — .x = trail length (cells), .y = core radius (cells)
//   3 foam blob   — .y = radius (cells)
//
// The tank shapes stay inside the 7x7-cell collision footprint: tracks and hull
// cover the same 6x5 cells as the simulation's tank sprite.
// ============================================================================

struct PSIn
{
    float4 pos : SV_POSITION;
    float2 local : TEXCOORD0;
    float4 color : COLOR0;
    float4 params : TEXCOORD1;
};

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

// Anti-aliased coverage of the inside of a signed distance (aa = one pixel in cells).
float Coverage(float dist, float aa)
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

float3 ApplyState(float3 rgb, float state)
{
    return state > 0.5 && state < 1.5 ? float3(1.0, 1.0, 1.0) : rgb;
}

float4 TankHull(float2 p, float3 team, float4 params, float aa)
{
    float2 facing = params.xy;
    float2 light = LocalLight(facing);
    float state = params.w;
    float4 result = float4(0.0, 0.0, 0.0, 0.0);

    static const float2 kHullCenter = float2(-0.5, 0.0);
    static const float2 kHullHalf = float2(2.45, 1.55);
    static const float2 kTrackHalf = float2(3.0, 0.52);
    static const float kTrackOffset = 1.98;

    // ---- Ground shadow: the silhouette, pushed away from the light ---------
    float2 shadowP = p + light * 0.75;
    float shadowDist = min(
        sdRoundBox(shadowP, kHullCenter, kHullHalf, 0.5),
        sdRoundBox(float2(shadowP.x, abs(shadowP.y)), float2(kHullCenter.x, kTrackOffset), kTrackHalf, 0.3));
    Over(result, float3(0.0, 0.0, 0.0), 0.42 * smoothstep(0.55, -0.35, shadowDist));

    // ---- Tracks --------------------------------------------------------------
    float2 trackP = float2(p.x, abs(p.y));
    float trackDist = sdRoundBox(trackP, float2(kHullCenter.x, kTrackOffset), kTrackHalf, 0.3);
    float tread = smoothstep(0.30, 0.70, abs(frac((p.x + params.z) * 1.25) - 0.5) * 2.0);
    float trackShade = 1.0 - 0.45 * smoothstep(0.15, 0.50, abs(trackP.y - kTrackOffset));
    float3 trackColor = lerp(float3(0.07, 0.07, 0.08), float3(0.27, 0.26, 0.25), tread) * trackShade;
    float3 outline = state > 1.5 ? kProtectedColor : kOutlineColor;
    Over(result, outline, Coverage(trackDist - 0.16, aa));
    Over(result, ApplyState(trackColor, state), Coverage(trackDist, aa));

    // ---- Hull ----------------------------------------------------------------
    float hullDist = sdRoundBox(p, kHullCenter, kHullHalf, 0.5);
    float2 e = float2(0.05, 0.0);
    float2 hullNormal = normalize(float2(
        sdRoundBox(p + e.xy, kHullCenter, kHullHalf, 0.5) - sdRoundBox(p - e.xy, kHullCenter, kHullHalf, 0.5),
        sdRoundBox(p + e.yx, kHullCenter, kHullHalf, 0.5) - sdRoundBox(p - e.yx, kHullCenter, kHullHalf, 0.5)) + 1e-5);
    float bevel = smoothstep(-0.75, -0.05, hullDist);
    float lit = dot(hullNormal, light);
    float3 hullColor = team * (0.80 + 0.42 * lit * bevel);
    hullColor += float3(0.16, 0.16, 0.16) * saturate(lit) * bevel;

    // Engine deck at the rear and a glacis line at the front.
    float deck = Coverage(sdRoundBox(p, float2(-2.05, 0.0), float2(0.42, 0.95), 0.12), aa);
    float louvre = smoothstep(0.25, 0.45, abs(frac(p.y * 2.2) - 0.5));
    hullColor = lerp(hullColor, team * lerp(0.30, 0.52, louvre), deck);
    float glacis = Coverage(abs(p.x - 1.25) - 0.06, aa) * step(abs(p.y), 1.2);
    hullColor = lerp(hullColor, team * 0.50, glacis * 0.8);

    Over(result, outline, Coverage(hullDist - 0.16, aa));
    Over(result, ApplyState(hullColor, state), Coverage(hullDist, aa));
    return result;
}

float4 TankTurret(float2 p, float3 team, float4 params, float aa)
{
    float2 light = LocalLight(params.xy);
    float state = params.w;
    float4 result = float4(0.0, 0.0, 0.0, 0.0);
    float3 outline = state > 1.5 ? kProtectedColor : kOutlineColor;

    // ---- Barrel --------------------------------------------------------------
    static const float2 kBarrelCenter = float2(2.35, 0.0);
    static const float2 kBarrelHalf = float2(2.0, 0.33);
    float barrelDist = sdRoundBox(p, kBarrelCenter, kBarrelHalf, 0.12);
    float barrelLit = 0.62 + 0.36 * saturate(-p.y / kBarrelHalf.y * sign(light.y) * 0.5 + 0.5);
    float3 barrelColor = lerp(team * 0.45, float3(0.34, 0.35, 0.37), 0.55) * barrelLit;
    // Bright muzzle tip: the aiming cue the old one-pixel barrel tip gave.
    barrelColor = lerp(barrelColor, float3(0.95, 0.92, 0.11), smoothstep(3.55, 3.75, p.x));
    Over(result, outline, Coverage(barrelDist - 0.14, aa));
    Over(result, ApplyState(barrelColor, state), Coverage(barrelDist, aa));

    // ---- Dome ----------------------------------------------------------------
    float domeDist = length(p) - 1.12;
    float2 domeNormal = p / max(length(p), 1e-4);
    float domeRise = saturate(length(p) / 1.12);
    float3 domeColor = team * (1.02 + 0.40 * dot(domeNormal, light) * domeRise);
    domeColor += float3(0.22, 0.22, 0.22) * smoothstep(0.55, 0.0, length(p - light * 0.42));
    float hatch = Coverage(abs(length(p + float2(0.25, 0.0)) - 0.42) - 0.05, aa);
    domeColor = lerp(domeColor, team * 0.45, hatch * 0.75);
    Over(result, outline, Coverage(domeDist - 0.15, aa));
    Over(result, ApplyState(domeColor, state), Coverage(domeDist, aa));
    return result;
}

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

float4 main(PSIn input) : SV_Target
{
    float2 p = input.local;
    float aa = max(length(fwidth(p)) * 0.7071, 1e-4);
    float kind = input.color.a;

    if (kind < 0.5)
        return TankHull(p, input.color.rgb, input.params, aa);
    if (kind < 1.5)
        return TankTurret(p, input.color.rgb, input.params, aa);
    if (kind < 2.5)
        return HotShot(p, input.color.rgb, input.params, aa);
    return FoamBlob(p, input.color.rgb, input.params, aa);
}
