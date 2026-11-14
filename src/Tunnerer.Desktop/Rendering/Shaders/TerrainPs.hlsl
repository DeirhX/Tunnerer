// ============================================================================
// TerrainPs.hlsl — Terrain pixel shader (native-resolution continuous render)
// ============================================================================
//
// Renders the terrain world-map into the scene render target at 1:1 screen
// resolution. Each terrain cell is PixelScale screen-pixels wide (typically 6).
//
// The cave/solid cut comes from the shared solid field (Common/TerrainEdge.hlsli):
// a smooth, anti-aliased edge that stays within half a cell of the collision
// edge. Solid pixels take their cell's exact colour; near the edge the colour
// is fetched from a cell inside the solid, so blank (cave) cells never show
// as blocks. With native smoothing off the cut is the exact cell grid.
//
// Material boundaries between solid materials (dirt / stone / ...) are NOT
// smoothed here; the post pass (TerrainAuxPass) re-draws every natural material
// from the blurred coverage texture (t2) and owns those edges.
//
// Entity pixels (tanks, bases, projectiles) are passed through unmodified;
// they are identified by alpha < 1.0 in the source texture.
//
// Bindings: t0 = world colours, t1 = aux (.g solid field used here). t2 (material
// coverage) is bound for both passes but only the post pass reads it.
// ============================================================================

#include "PostPs/Common/Registers.hlsli"

Texture2D sourceTex : FRAGMENT_TEXTURE(0);
Texture2D auxTex : FRAGMENT_TEXTURE(1);
// One bilinear-clamp sampler per texture, at the texture's index (see PostPs/Common/Bindings.hlsli).
SamplerState s0 : FRAGMENT_SAMPLER(0);
SamplerState s1 : FRAGMENT_SAMPLER(1);

#include "PostPs/Common/PostParams.hlsli"
#include "PostPs/Common/TerrainEdge.hlsli"

float4 main(float4 pos : SV_POSITION, float2 uv : TEXCOORD0) : SV_Target
{
    // ---- Screen → World coordinate mapping --------------------------------
    float2 screenPx = uv * ViewSize;
    float2 worldCell = (CameraPixels + screenPx) / max(1.0, PixelScale);

    int2 maxCell = int2(max(1.0, WorldSize.x), max(1.0, WorldSize.y)) - int2(1, 1);
    int2 cell = clamp(int2(floor(worldCell)), int2(0, 0), maxCell);
    float4 nearestSample = sourceTex.Load(int3(cell, 0));

    // Uses derivatives, so evaluated before any early-out.
    TerrainEdge edge = EvalTerrainEdge(worldCell);

    // ---- Entity early-out -------------------------------------------------
    // Entity pixels (tanks, projectiles, bases) carry alpha < 1.0.
    if (nearestSample.a < 0.999)
        return nearestSample;

    // Fail-safe against CPU/HLSL cbuffer drift: keep terrain unfiltered if
    // layout version does not match the shader expectation.
    if (abs(PostParamsLayoutVersion - kPostParamsLayoutVersion) > 0.001)
        return float4(nearestSample.rgb, 1.0);

    bool smooth = NativeContinuousParams.x > 0.5 && NativeContinuousParams.w > 0.5;
    float coverage;
    float3 solidColor;
    if (smooth)
    {
        coverage = edge.coverage;
        // Within a cell of the edge, take the colour from a cell inside the solid.
        float push = max(0.0, 0.75 - edge.distCells);
        int2 solidCell = clamp(int2(floor(worldCell + edge.normal * push)), int2(0, 0), maxCell);
        // Sampled at the texel centre, so bilinear returns the cell's exact colour.
        float4 inside = sourceTex.Sample(s0, (float2(solidCell) + 0.5) / WorldSize);
        solidColor = inside.a < 0.999 ? nearestSample.rgb : inside.rgb;
    }
    else
    {
        // Exact cells: the stored field is on the correct side of 0.5 at every cell.
        coverage = step(0.5, auxTex.Load(int3(cell, 0)).g);
        solidColor = nearestSample.rgb;
    }

    return float4(lerp(kTerrainCaveColor, solidColor, coverage), 1.0);
}
