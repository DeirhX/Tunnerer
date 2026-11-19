// ============================================================================
// ImGuiPs.hlsl — Dear ImGui pixel shader (SDL_GPU backend)
// ============================================================================

#include "PostPs/Common/Registers.hlsli"

Texture2D t0 : FRAGMENT_TEXTURE(0);
SamplerState s0 : FRAGMENT_SAMPLER(0);

float4 main(float4 pos : SV_POSITION, float4 col : COLOR0, float2 uv : TEXCOORD0) : SV_Target
{
    return col * t0.Sample(s0, uv);
}
