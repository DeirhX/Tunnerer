// ============================================================================
// Common/Bindings.hlsli — Texture, sampler, and cbuffer declarations
// ============================================================================
//
// Shared by all post-processing passes. Declares the input textures, the
// bilinear sampler, and includes the shared PostParams constant buffer.
// ============================================================================

Texture2D sceneTex : register(t0);  // Composited scene (TerrainPs output + entities)
Texture2D auxTex : register(t1);    // Per-cell terrain aux (.r=heat, .g=cave/solid SDF, .b=exact material code (read with Load only), .a=scorch)
Texture2D coverageTex : register(t2); // Per-cell blurred material coverage (.r=stone, .g=concrete, .b=gold, .a=dirt; sums to 1 over natural solid cells)
SamplerState s0 : register(s0);     // Bilinear-clamp sampler

#include "PostParams.hlsli"
