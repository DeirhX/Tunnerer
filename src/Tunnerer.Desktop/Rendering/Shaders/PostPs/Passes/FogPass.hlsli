// ============================================================================
// Passes/FogPass.hlsli — Fog of war
// ============================================================================
//
// Runs last, over the finished terrain. fogTex holds one texel per world cell:
//   .r = how well the player's side sees the cell (0 out of sight, 1 in plain
//        sight, fading over the last cells of a source's reach)
//   .g = 1 once the cell has been explored
//
// Three looks, blended by those two values:
//   in sight    — the frame as it is
//   remembered  — explored but out of sight: drained of most colour toward a
//                 cool blue-grey and dimmed. Some colour is kept so stone, dirt
//                 and gold can still be told apart, and the blue cast keeps
//                 remembered ground clear of the orange of live heat.
//   unknown     — never seen: near black
//
// The terrain under remembered ground is itself the last-seen terrain (the CPU
// stops updating cells that are out of sight), so this pass only tones it.
// Entities are drawn after this pass; the CPU leaves out the ones out of sight.
// ============================================================================

static const float3 kFogUnknownColor = float3(0.012, 0.014, 0.020);
static const float3 kFogMemoryTint = float3(0.74, 0.86, 1.00);
static const float kFogMemoryDesaturate = 0.75;
static const float kFogMemoryBrightness = 0.55;
// Half-width, in cells, of the blur that softens the stepped edge of the per-cell data.
static const float kFogSoftenCells = 0.75;

float2 SampleFog(float2 worldCell)
{
    return fogTex.Sample(s3, worldCell / max(WorldSize, float2(1.0, 1.0))).rg;
}

void ApplyFogPass(float2 uv, inout float3 color)
{
    float2 worldCell = (CameraPixels + uv * ViewSize) / max(1.0, PixelScale);
    float2 fog = SampleFog(worldCell) * 2.0
        + SampleFog(worldCell + float2(kFogSoftenCells, 0.0))
        + SampleFog(worldCell - float2(kFogSoftenCells, 0.0))
        + SampleFog(worldCell + float2(0.0, kFogSoftenCells))
        + SampleFog(worldCell - float2(0.0, kFogSoftenCells));
    fog /= 6.0;

    float seen = smoothstep(0.0, 1.0, fog.r);
    float explored = smoothstep(0.0, 1.0, fog.g);

    float luma = dot(color, kLumaWeights);
    float3 remembered = lerp(color, luma * kFogMemoryTint, kFogMemoryDesaturate) * kFogMemoryBrightness;
    float3 unseen = lerp(kFogUnknownColor, remembered, explored);
    color = lerp(unseen, color, seen);
}
