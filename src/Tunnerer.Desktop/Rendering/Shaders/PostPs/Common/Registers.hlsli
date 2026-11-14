// ============================================================================
// Common/Registers.hlsli — Register declarations for SDL_GPU
// ============================================================================
//
// The HLSL is compiled with TUNNERER_SDL_GPU defined by SDL_shadercross (tools/Tunnerer.ShaderCompiler) to
// DXIL, SPIR-V and MSL. SDL_GPU expects resources in fixed spaces: vertex
// uniforms in space1, fragment textures and samplers in space2, fragment
// uniforms in space3 (see SDL_CreateGPUShader).
//
// Declare every resource with these macros, never with register() directly.
// ============================================================================

#ifndef TUNNERER_REGISTERS_HLSLI
#define TUNNERER_REGISTERS_HLSLI

#ifdef TUNNERER_SDL_GPU
#define VERTEX_UNIFORMS(n)   register(b##n, space1)
#define FRAGMENT_TEXTURE(n)  register(t##n, space2)
#define FRAGMENT_SAMPLER(n)  register(s##n, space2)
#define FRAGMENT_UNIFORMS(n) register(b##n, space3)
#endif

#endif
