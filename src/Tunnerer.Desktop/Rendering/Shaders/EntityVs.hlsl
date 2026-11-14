// ============================================================================
// EntityVs.hlsl — Entity layer vertex shader
// ============================================================================
//
// Passes entity quads through unchanged. The CPU (EntityLayerBuilder) emits
// clip-space positions and the per-entity data the pixel shader needs, so the
// entity layer has no constant buffer.
// ============================================================================

struct VSIn
{
    float2 pos : POSITION;      // clip space
    float2 local : TEXCOORD0;   // entity-local position in world cells (+x = facing / travel direction)
    float4 color : COLOR0;      // .rgb = entity colour, .a = shape kind
    float4 params : TEXCOORD1;  // per-kind parameters (see EntityPs.hlsl)
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
    o.pos = float4(input.pos, 0.0, 1.0);
    o.local = input.local;
    o.color = input.color;
    o.params = input.params;
    return o;
}
