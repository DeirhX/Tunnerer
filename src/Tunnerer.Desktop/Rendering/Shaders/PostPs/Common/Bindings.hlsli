// ============================================================================
// Common/Bindings.hlsli — Texture, sampler, and cbuffer declarations
// ============================================================================
//
// Shared by all post-processing passes. Declares the input textures, the
// bilinear sampler, and includes the shared PostParams constant buffer.
// ============================================================================

#include "Registers.hlsli"

Texture2D sceneTex : FRAGMENT_TEXTURE(0);    // Composited scene (TerrainPs output + entities)
Texture2D auxTex : FRAGMENT_TEXTURE(1);      // Per-cell terrain aux (.r=heat, .g=solid field, see TerrainEdge.hlsli, .b=exact material code (read with Load only), .a=scorch)
Texture2D coverageTex : FRAGMENT_TEXTURE(2); // Per-cell blurred material coverage (.r=stone, .g=concrete, .b=dirt; sums to 1 over natural solid cells)
Texture2D fogTex : FRAGMENT_TEXTURE(3);      // Per-cell fog of war (.r = how well the cell is seen, .g = explored), see Passes/FogPass.hlsli
// One sampler per texture, at the texture's index: SDL_GPU pairs them, and a texture without a sampler would be
// reflected as a storage texture. All four are the same bilinear-clamp sampler on the CPU side.
SamplerState s0 : FRAGMENT_SAMPLER(0);       // sceneTex
SamplerState s1 : FRAGMENT_SAMPLER(1);       // auxTex
SamplerState s2 : FRAGMENT_SAMPLER(2);       // coverageTex
SamplerState s3 : FRAGMENT_SAMPLER(3);       // fogTex

#include "PostParams.hlsli"
