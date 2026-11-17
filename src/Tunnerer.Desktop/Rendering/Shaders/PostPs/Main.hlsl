// ============================================================================
// PostPs/Main.hlsl — Full-screen post-processing pixel shader
// ============================================================================
//
// Entry point for the post-processing pass. Runs once per screen pixel after
// the terrain (TerrainPs) and entities have been composited into sceneTex.
//
// Pipeline order:
//   1. Heat haze distortion (ComputeTankHeatHaze + ApplyOutlineHeatDistortion)
//   2. Vignette (ApplyVignettePass)
//   3. Terrain aux: edge shading, material overlays, heat glow (ApplyTerrainAuxPass)
//   4. Tank heat glow (ApplyTankHeatGlowPass)
//   5. Fog of war (ApplyFogPass)
//
// Inputs:
//   sceneTex (t0) — composited scene (terrain + entities)
//   auxTex   (t1) — per-cell terrain data (.r = signed heat, see kHeatZero, .g = solid field, .b = exact material code, .a = scorch)
//   coverageTex (t2) — per-cell blurred natural-material coverage (.r stone, .g concrete, .b dirt)
//   fogTex   (t3) — per-cell fog of war (.r = how well the cell is seen, .g = explored)
//
// Terrain vs entity discrimination:
//   sceneSample.a >= 1.0 → terrain pixel   (terrainFactor = 1)
//   sceneSample.a <  1.0 → entity pixel    (terrainFactor = 0)
//   This prevents terrain-only effects from bleeding onto tanks/projectiles.
// ============================================================================

#include "Common/Bindings.hlsli"
#include "Common/Constants.hlsli"

#include "Common/MathNoise.hlsli"
#include "Common/ColorRamps.hlsli"
#include "Common/TerrainEdge.hlsli"

#include "Materials/TerrainShared.hlsli"
#include "Materials/StoneMaterial.hlsli"
#include "Materials/ConcreteMaterial.hlsli"
#include "Materials/DirtMaterial.hlsli"
#include "Materials/GoldMaterial.hlsli"
#include "Materials/OreMaterial.hlsli"
#include "Materials/TunnelFloorMaterial.hlsli"
#include "Materials/BaseMaterial.hlsli"

#include "Passes/DistortionPass.hlsli"
#include "Passes/VignettePass.hlsli"
#include "Passes/TerrainAuxPass.hlsli"
#include "Passes/TankGlowPass.hlsli"
#include "Passes/FogPass.hlsli"

float4 main(float4 pos : SV_POSITION, float2 uv : TEXCOORD0) : SV_Target
{
    // ---- Heat haze UV distortion ------------------------------------------
    // Distortion is part of the tank-glow feature set. Keep it disabled when
    // the tank-glow pass is toggled off so "all passes off" is truly neutral.
    float2 hazeOffset = float2(0.0, 0.0);
    if (PostTankGlowEnabled > 0.5)
    {
        float outlineHeatMask;
        float distortionEnabled;
        ComputeTankHeatHaze(uv, hazeOffset, outlineHeatMask, distortionEnabled);
        ApplyOutlineHeatDistortion(uv, distortionEnabled, outlineHeatMask, hazeOffset);
    }

    float2 hazeUv = clamp(uv + hazeOffset, TexelSize * 0.5, 1.0 - TexelSize * 0.5);
    float4 sceneSample = sceneTex.Sample(s0, hazeUv);
    float3 baseColor = sceneSample.rgb;
    float3 color = baseColor;
    float terrainFactor = step(kTerrainAlphaThreshold, sceneSample.a);

    // Fail-safe against CPU/HLSL cbuffer drift: disable post effects when
    // layout version does not match the shader expectation.
    if (abs(PostParamsLayoutVersion - kPostParamsLayoutVersion) > 0.001)
        return float4(baseColor, 1.0);

    // ---- Post-process chain -----------------------------------------------
    if (PostVignetteEnabled > 0.5)
        ApplyVignettePass(uv, color);
    if (PostTerrainAuxEnabled > 0.5 || PostTerrainHeatEnabled > 0.5 || PostThermalRegionsEnabled > 0.5)
        ApplyTerrainAuxPass(uv, terrainFactor, PostTerrainAuxEnabled > 0.5, PostTerrainHeatEnabled > 0.5, PostThermalRegionsEnabled > 0.5, color);
    if (PostTankGlowEnabled > 0.5)
        ApplyTankHeatGlowPass(uv, color);
    if (PostFogEnabled > 0.5)
        ApplyFogPass(uv, color);

    return float4(color, 1.0);
}
