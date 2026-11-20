// The numeric glyphs are packed into vertex parameters by PowerLabelQuads.
float PowerGlyphInk(float2 dot, float2 bitmap)
{
    int2 at = int2(floor(dot));
    float ink = 0.0;
    if (all(at >= 0) && at.x < 5 && at.y < 7)
    {
        // The upper-left dot is the high bit and the lower-right dot is the low bit.
        int bit = (6 - at.y) * 5 + 4 - at.x;
        uint bits = (uint)round(bit < 20 ? bitmap.x : bitmap.y);
        ink = (float)((bits >> (bit % 20)) & 1u);
    }
    return ink;
}

float4 PowerLabel(float2 p, float3 color, float4 params)
{
    float ink = 1.0;
    float coverage = 1.0;
    if (params.z > 0.0)
    {
        float2 dot = p * params.z;
        ink = PowerGlyphInk(dot, params.xy);
        float shadow = PowerGlyphInk(dot - 0.75, params.xy);
        coverage = max(ink, shadow * 0.8);
    }
    return float4(color * (ink * params.w), coverage * params.w);
}
