#include "PostPs/Common/Registers.hlsli"

Texture2D t0 : FRAGMENT_TEXTURE(0);
SamplerState s0 : FRAGMENT_SAMPLER(0);

float4 main(float4 pos : SV_POSITION, float2 uv : TEXCOORD0) : SV_Target
{
    return t0.Sample(s0, uv);
}
