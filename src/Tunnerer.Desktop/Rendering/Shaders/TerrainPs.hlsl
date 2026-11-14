// ============================================================================
// TerrainPs.hlsl — Terrain pixel shader (native-resolution continuous render)
// ============================================================================
//
// Renders the terrain world-map into the scene render target at 1:1 screen
// resolution. Each terrain cell is PixelScale screen-pixels wide (typically 6).
//
// Two sampling strategies are blended:
//   • Nearest-neighbor — preserves sharp per-cell texture detail deep inside
//     solid terrain.
//   • Bilinear — smooths color transitions near the cave/solid boundary so
//     the 6-pixel cell grid is less visible.
//
// A Gaussian-blurred SDF (stored in auxTex.g) drives alpha-blending between
// "cave" (dark empty space) and "solid" (dirt / stone / energy) terrain.
// The SDF's 0.5 isoline is further smoothed along its tangent direction to
// reduce the axis-aligned staircase inherent in cell-grid boundaries.
//
// Material boundaries between solid materials (dirt / stone / ...) are NOT
// smoothed here: this pass keeps each cell's exact colour. The post pass
// (TerrainAuxPass) re-draws every natural material from the blurred coverage
// texture (t2) and owns those edges.
//
// Entity pixels (tanks, bases, projectiles) are passed through unmodified;
// they are identified by alpha < 1.0 in the source texture.
//
// Bindings: t0 = world colours, t1 = aux (.g SDF used here). t2 (material
// coverage) is bound for both passes but only the post pass reads it.
// ============================================================================

#include "PostPs/Common/Registers.hlsli"

Texture2D sourceTex : FRAGMENT_TEXTURE(0);
Texture2D auxTex : FRAGMENT_TEXTURE(1);
// One bilinear-clamp sampler per texture, at the texture's index (see PostPs/Common/Bindings.hlsli).
SamplerState s0 : FRAGMENT_SAMPLER(0);
SamplerState s1 : FRAGMENT_SAMPLER(1);

#include "PostPs/Common/PostParams.hlsli"

float4 main(float4 pos : SV_POSITION, float2 uv : TEXCOORD0) : SV_Target
{
    bool sharpMode = NativeContinuousParams.x <= 1e-5 && NativeContinuousParams.y <= 1e-5;
    // Curving only makes sense when smoothing is also active; otherwise
    // "all passes off" should stay strictly nearest/uncurved.
    bool nativeCurvingEnabled = !sharpMode && NativeContinuousParams.w > 0.5;

    // ---- Screen → World coordinate mapping --------------------------------
    float2 screenPx = uv * ViewSize;
    float2 worldCell = (CameraPixels + screenPx) / max(1.0, PixelScale);

    // ---- Nearest-neighbor fetch -------------------------------------------
    // Avoids bilinear bleed across entity / terrain type boundaries.
    int2 maxCell = int2(max(1.0, WorldSize.x), max(1.0, WorldSize.y)) - int2(1, 1);
    int2 cell = clamp(int2(floor(worldCell)), int2(0, 0), maxCell);
    float4 nearestSample = sourceTex.Load(int3(cell, 0));
    float3 nearestColor = nearestSample.rgb;
    float sdfNearest = auxTex.Load(int3(cell, 0)).g;

    // ---- Entity early-out -------------------------------------------------
    // Entity pixels (tanks, projectiles, bases) carry alpha < 1.0.
    float entityFlag = nearestSample.a;
    if (entityFlag < 0.999)
        return float4(nearestColor, entityFlag);

    // Fail-safe against CPU/HLSL cbuffer drift: keep terrain unfiltered if
    // layout version does not match the shader expectation.
    if (abs(PostParamsLayoutVersion - kPostParamsLayoutVersion) > 0.001)
        return float4(nearestColor, 1.0);

    // ---- Bilinear fetch ---------------------------------------------------
    float2 texUv = worldCell / WorldSize;
    float3 bilinearColor = sourceTex.Sample(s0, texUv).rgb;

    // ---- SDF fetch & gradient ---------------------------------------------
    // auxTex.g holds a Gaussian-blurred signed-distance field:
    //   0.0 = deep cave, 0.5 = boundary, 1.0 = deep solid.
    float2 sdfUv = texUv;
    float sdf = nativeCurvingEnabled ? auxTex.Sample(s1, sdfUv).g : sdfNearest;
    float2 cellPx = 1.0 / WorldSize;

    // 3-cell central difference for a stable SDF gradient.
    float2 gradStep = cellPx * 1.5;
    float2 sdfGrad;
    sdfGrad.x = auxTex.Sample(s1, sdfUv + float2(gradStep.x, 0)).g
              - auxTex.Sample(s1, sdfUv - float2(gradStep.x, 0)).g;
    sdfGrad.y = auxTex.Sample(s1, sdfUv + float2(0, gradStep.y)).g
              - auxTex.Sample(s1, sdfUv - float2(0, gradStep.y)).g;

    // ---- Tangent-aligned SDF smoothing ------------------------------------
    // Average the SDF at 6 points along the boundary tangent (±0.75, ±1.5,
    // ±2.0 cells). Merges staircase steps without over-rounding corners.
    float gradLen = length(sdfGrad);
    if (!sharpMode && nativeCurvingEnabled && gradLen > 0.001)
    {
        float2 tangent = float2(-sdfGrad.y, sdfGrad.x) / gradLen;
        float2 t1 = tangent * cellPx * 2.0;
        float2 t2 = tangent * cellPx * 1.5;
        float2 t3 = tangent * cellPx * 0.75;
        float st1 = auxTex.Sample(s1, sdfUv + t1).g;
        float st2 = auxTex.Sample(s1, sdfUv - t1).g;
        float st3 = auxTex.Sample(s1, sdfUv + t2).g;
        float st4 = auxTex.Sample(s1, sdfUv - t2).g;
        float st5 = auxTex.Sample(s1, sdfUv + t3).g;
        float st6 = auxTex.Sample(s1, sdfUv - t3).g;
        sdf = (sdf * 3.0 + st5 + st6 + st3 + st4 + (st1 + st2) * 0.5) / 8.0;
    }

    // Small inward bias to avoid a thin bright fringe on the cave side.
    if (!sharpMode && nativeCurvingEnabled)
        sdf -= 0.012;

    // ---- Cave / solid alpha -----------------------------------------------
    // fwidth-adaptive softness: wider transitions where the SDF changes
    // rapidly in screen space (shallow-angle boundaries).
    float alpha;
    if (sharpMode || !nativeCurvingEnabled)
    {
        // Hard cave/solid cut with no soft transition when smoothing/curving
        // is disabled so visuals match collision-occupied cells.
        alpha = step(0.5, sdf);
    }
    else
    {
        float fw = fwidth(sdf);
        float baseSoftness = max(0.10, NativeContinuousParams.x);
        float edgeSoftness = baseSoftness + fw * 3.0;
        alpha = smoothstep(0.5 - edgeSoftness, 0.5 + edgeSoftness, sdf);
    }

    // ---- Solid-cell alpha floor --------------------------------------------
    // The Gaussian SDF can push small isolated clumps below the visibility
    // threshold, making them invisible while still colliding. Guarantee a
    // minimum alpha for any cell that has a material assignment (auxTex.b > 0).
    float cellMaterial = auxTex.Load(int3(cell, 0)).b;
    if (cellMaterial > 0.01 && SolidAlphaFloor > 0.0)
        alpha = max(alpha, SolidAlphaFloor);

    // ---- Bilinear / nearest blending weight -------------------------------
    // Near the SDF boundary: prefer bilinear (smooth color transitions).
    // Deep inside solid terrain: prefer nearest (sharp cell textures).
    float bilinearWeight = 0.0;
    if (!sharpMode && nativeCurvingEnabled)
    {
        float boundaryProximity = 1.0 - abs(sdf * 2.0 - 1.0);
        bilinearWeight = smoothstep(0.0, 0.6, boundaryProximity) * NativeContinuousParams.y;
    }

    float3 solidColor = lerp(nearestColor, bilinearColor, saturate(bilinearWeight));

    // ---- Final compositing ------------------------------------------------
    float3 caveColor = float3(0.055, 0.055, 0.063);
    float3 color = lerp(caveColor, solidColor, alpha);
    return float4(color, 1.0);
}
