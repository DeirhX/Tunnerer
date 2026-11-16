// ============================================================================
// Common/TerrainEdge.hlsli — The one definition of the cave/solid edge
// ============================================================================
//
// auxTex.g holds TerrainSolidField: a narrow blur of solid occupancy whose
// cells are kept on their own side of 0.5, so its bilinear 0.5 contour is a
// smooth curve within half a cell of the collision edge. Everything that needs
// the edge (TerrainPs's cave/solid cut, material compositing, rim light,
// contact shadow) evaluates it here so they all agree.
//
// Newly regrown dirt grows out of the old edge instead of popping in a whole
// cell at once: coverageTex.a holds each cell's regrow stamp (TerrainGrowthFade),
// and the field is pulled back around fresh cells while their fade runs.
//
// Requires auxTex / s1, coverageTex / s2 and the PostParams cbuffer (WorldSize,
// Time) to be declared. EvalTerrainEdge uses fwidth: call it outside divergent
// flow control.
// ============================================================================

// Flat cave colour drawn by TerrainPs; TerrainAuxPass replaces it with the tunnel floor.
static const float3 kTerrainCaveColor = float3(0.085, 0.078, 0.070);

// Must match TerrainGrowthFade.
static const float kGrowTicksPerSecond = 32.0;
static const float kGrowFadeTicks = 16.0;     // 0.5 s
// How far a just-regrown cell's field is pulled down: enough to put its centre back in the cave.
static const float kGrowDepth = 0.45;

// 1 for a cell that regrew just now, easing to 0 over the fade; 0 for settled cells.
float GrowFreshnessAtCell(int2 c)
{
    int2 cellMax = int2(max(1.0, WorldSize.x), max(1.0, WorldSize.y)) - int2(1, 1);
    c = clamp(c, int2(0, 0), cellMax);
    // Sampled at the texel centre, so bilinear returns the exact stamp byte.
    float stamp = round(coverageTex.Sample(s2, (float2(c) + 0.5) / WorldSize).a * 255.0);
    if (stamp < 0.5)
        return 0.0;
    float now = Time * kGrowTicksPerSecond;
    float age = fmod(fmod(floor(now) - (stamp - 1.0), 255.0) + 255.0, 255.0) + frac(now);
    return 1.0 - smoothstep(0.0, kGrowFadeTicks, age);
}

// Bilinear blend of the four cells around a position, like the field itself.
float GrowFreshness(float2 worldCell)
{
    float2 p = worldCell - 0.5;
    int2 c = int2(floor(p));
    float2 t = p - floor(p);
    return lerp(
        lerp(GrowFreshnessAtCell(c), GrowFreshnessAtCell(c + int2(1, 0)), t.x),
        lerp(GrowFreshnessAtCell(c + int2(0, 1)), GrowFreshnessAtCell(c + int2(1, 1)), t.x),
        t.y);
}

// Signed field at a world-cell position: + inside solid, - in cave, 0 on the edge.
float TerrainSolidField(float2 worldCell)
{
    return auxTex.Sample(s1, worldCell / WorldSize).g - 0.5;
}

struct TerrainEdge
{
    float coverage;   // 0 cave .. 1 solid, anti-aliased over about one screen pixel
    float distCells;  // signed distance to the edge in cells, + inside solid
    float2 normal;    // unit direction pointing into solid
};

TerrainEdge EvalTerrainEdge(float2 worldCell)
{
    TerrainEdge e;
    float f = TerrainSolidField(worldCell) - kGrowDepth * GrowFreshness(worldCell);
    float2 g = float2(
        TerrainSolidField(worldCell + float2(0.5, 0.0)) - TerrainSolidField(worldCell - float2(0.5, 0.0)),
        TerrainSolidField(worldCell + float2(0.0, 0.5)) - TerrainSolidField(worldCell - float2(0.0, 0.5)));
    float gl = max(length(g), 0.02);
    e.normal = g / gl;
    e.distCells = f / gl;
    e.coverage = saturate(f / max(fwidth(f), 1e-4) + 0.5);
    return e;
}
