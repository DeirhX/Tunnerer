// ============================================================================
// EntityVs.hlsl — Entity layer vertex shader
// ============================================================================
//
// Places entity quads. The CPU (EntityLayerBuilder) emits each corner in world
// cells, where its entity is at the start of the current simulation step, plus
// the entity's motion through the step (cells per step). The shader draws it at
//
//     cell = pos + motion * StepAlpha
//
// and turns that into clip space with the camera, which moves the same way
// (CameraPixelsNow), so everything is drawn at the same point in time and the
// display can run faster than the simulation. The per-entity data the pixel
// shader needs passes through unchanged.
//
// PostParams (the same block the pixel shaders read) supplies StepAlpha, the
// camera, the pixel scale and the view size.
// ============================================================================

#define EMBERWARD_POST_PARAMS_VERTEX
#include "PostPs/Common/PostParams.hlsli"

struct VSIn
{
    float2 pos : POSITION;      // world cells, at the start of the step
    float2 local : TEXCOORD0;   // entity-local position in world cells (+x = facing / travel direction)
    float4 color : COLOR0;      // .rgb = entity colour, .a = shape kind
    float4 params : TEXCOORD1;  // per-kind parameters (see EntityPs.hlsl)
    float2 motion : TEXCOORD2;  // cells moved in one simulation step
};

struct VSOut
{
    float4 pos : SV_POSITION;
    float2 local : TEXCOORD0;
    float4 color : COLOR0;
    float4 params : TEXCOORD1;
};

VSOut main(VSIn input)
{
    VSOut o;
    float2 cell = input.pos + input.motion * StepAlpha;
    float2 screenPx = cell * PixelScale - CameraPixelsNow();
    float2 clip = float2(screenPx.x * 2.0 / ViewSize.x - 1.0, 1.0 - screenPx.y * 2.0 / ViewSize.y);
    o.pos = float4(clip, 0.0, 1.0);
    o.local = input.local;
    o.color = input.color;
    o.params = input.params;
    return o;
}
