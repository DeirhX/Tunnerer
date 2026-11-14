// ============================================================================
// Common/Registers.hlsli — Register declarations for both shader compilers
// ============================================================================
//
// The same HLSL is compiled two ways:
//   • fxc for the DX11 backend (Windows only). Shader model 5.0 has no
//     register spaces.
//   • SDL_shadercross for the SDL_GPU backend, with TUNNERER_SDL_GPU defined
//     (tools/Tunnerer.ShaderCompiler). SDL_GPU expects resources in fixed
//     spaces: vertex uniforms in space1, fragment textures and samplers in
//     space2, fragment uniforms in space3 (see SDL_CreateGPUShader).
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
#else
#define VERTEX_UNIFORMS(n)   register(b##n)
#define FRAGMENT_TEXTURE(n)  register(t##n)
#define FRAGMENT_SAMPLER(n)  register(s##n)
#define FRAGMENT_UNIFORMS(n) register(b##n)
#endif

#endif
