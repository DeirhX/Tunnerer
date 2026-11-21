// ============================================================================
// Entity/ShieldVeins.hlsli — The vein lattice a shield shows when it is hit
// ============================================================================
//
// A shield is invisible until it takes a hit; then the hit lights its veins, a hexagonal lattice, across the whole
// shield, with a wavefront running out from the point of impact. The lattice is analytic (no loops, no textures), so
// it is cheap and sharp at any scale.
// ============================================================================

#ifndef EMBERWARD_ENTITY_SHIELDVEINS_HLSLI
#define EMBERWARD_ENTITY_SHIELDVEINS_HLSLI

// Distance, in lattice units, from `p` to the nearest hexagon edge: 0 on a vein, about 0.5 at a cell's middle.
float HexVeinDistance(float2 p)
{
    const float2 r = float2(1.0, 1.7320508);
    const float2 h = float2(0.5, 0.8660254);
    float2 a = p - r * floor(p / r) - h;
    float2 q = p - h;
    float2 b = q - r * floor(q / r) - h;
    float2 g = (dot(a, a) < dot(b, b)) ? a : b;
    float2 ag = abs(g);
    return 0.5 - max(dot(ag, float2(0.5, 0.8660254)), ag.x);
}

// 1 on a vein, falling to 0 within `width` lattice units of it.
float VeinLines(float2 p, float width)
{
    return 1.0 - smoothstep(0.0, width, HexVeinDistance(p));
}

// A wavefront that leaves `hit` and travels `speed` cells over the pulse (age 0..1), `width` cells thick.
float VeinFront(float2 p, float2 hit, float age, float speed, float width)
{
    float offset = (length(p - hit) - age * speed) / width;
    return exp(-offset * offset);
}

#endif // EMBERWARD_ENTITY_SHIELDVEINS_HLSLI
