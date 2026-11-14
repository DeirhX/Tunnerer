// ============================================================================
// Common/Bindings.hlsli — Texture, sampler, and cbuffer declarations
// ============================================================================
//
// Shared by all post-processing passes. Declares the input textures, the
// bilinear sampler, and includes the shared PostParams constant buffer.
// ============================================================================

#include "Registers.hlsli"

Texture2D sceneTex : FRAGMENT_TEXTURE(0);    // Composited scene (TerrainPs output + entities)
Texture2D auxTex : FRAGMENT_TEXTURE(1);      // Per-cell terrain aux (.r=heat, .g=cave/solid SDF, .b=exact material code (read with Load only), .a=scorch)
Texture2D coverageTex : FRAGMENT_TEXTURE(2); // Per-cell blurred material coverage (.r=stone, .g=concrete, .b=dirt; sums to 1 over natural solid cells)
// One sampler per texture, at the texture's index: SDL_GPU pairs them, and a texture without a sampler would be
// reflected as a storage texture. All three are the same bilinear-clamp sampler on the CPU side.
SamplerState s0 : FRAGMENT_SAMPLER(0);       // sceneTex
SamplerState s1 : FRAGMENT_SAMPLER(1);       // auxTex
SamplerState s2 : FRAGMENT_SAMPLER(2);       // coverageTex

#include "PostParams.hlsli"
