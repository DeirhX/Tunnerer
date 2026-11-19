// ============================================================================
// Common/Registers.hlsli — Register declarations for SDL_GPU
// ============================================================================
//
// SDL_shadercross (tools/Emberward.ShaderCompiler) compiles the HLSL to DXIL,
// SPIR-V and MSL. SDL_GPU expects resources in fixed spaces: vertex uniforms
// in space1, fragment textures and samplers in space2, fragment uniforms in
// space3 (see SDL_CreateGPUShader). These macros keep the spaces in one place.
//
// Declare every resource with these macros, never with register() directly.
// ============================================================================

#ifndef EMBERWARD_REGISTERS_HLSLI
#define EMBERWARD_REGISTERS_HLSLI

#define VERTEX_UNIFORMS(n)   register(b##n, space1)
#define FRAGMENT_TEXTURE(n)  register(t##n, space2)
#define FRAGMENT_SAMPLER(n)  register(s##n, space2)
#define FRAGMENT_UNIFORMS(n) register(b##n, space3)

#endif
