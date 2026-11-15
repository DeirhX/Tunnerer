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
// Requires auxTex / s1 and the PostParams cbuffer (WorldSize) to be declared.
// EvalTerrainEdge uses fwidth: call it outside divergent flow control.
// ============================================================================

static const float3 kTerrainCaveColor = float3(0.055, 0.055, 0.063);

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
    float f = TerrainSolidField(worldCell);
    float2 g = float2(
        TerrainSolidField(worldCell + float2(0.5, 0.0)) - TerrainSolidField(worldCell - float2(0.5, 0.0)),
        TerrainSolidField(worldCell + float2(0.0, 0.5)) - TerrainSolidField(worldCell - float2(0.0, 0.5)));
    float gl = max(length(g), 0.02);
    e.normal = g / gl;
    e.distCells = f / gl;
    e.coverage = saturate(f / max(fwidth(f), 1e-4) + 0.5);
    return e;
}
