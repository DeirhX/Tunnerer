// ============================================================================
// ImGuiVs.hlsl — Dear ImGui vertex shader (SDL_GPU backend)
// ============================================================================
//
// Maps ImGui's pixel-space vertices to clip space with a scale and translate,
// as upstream imgui_impl_sdlgpu3 does. Vertex layout matches ImDrawVert:
// float2 pos, float2 uv, RGBA8 colour.
// ============================================================================

#include "PostPs/Common/Registers.hlsli"

cbuffer ImGuiTransform : VERTEX_UNIFORMS(0)
{
    float2 Scale;
    float2 Translate;
};

struct VSIn
{
    float2 pos : POSITION;
    float2 uv : TEXCOORD0;
    float4 col : COLOR0;
};

struct VSOut
{
    float4 pos : SV_POSITION;
    float4 col : COLOR0;
    float2 uv : TEXCOORD0;
};

VSOut main(VSIn input)
{
    VSOut o;
    o.pos = float4(input.pos * Scale + Translate, 0.0, 1.0);
    o.col = input.col;
    o.uv = input.uv;
    return o;
}
