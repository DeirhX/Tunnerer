// ============================================================================
// Materials/TunnelFloorMaterial.hlsli — The floor of dug-out tunnels and caves
// ============================================================================
//
// A dim, recessed floor of packed spoil instead of a flat black void. It has
// to stay quiet: tanks, projectiles and pickups are drawn over it, so it uses
// only broad, low-contrast variation and never competes with the walls.
//
// Layer stack:
//   1. Tint        — a dark, desaturated echo of the rock the tunnel was cut
//                    through (coverageTex reaches ~4 cells into the cave);
//                    a neutral umber farther out.
//   2. Mottle      — broad, soft patches of packed and loose spoil (+-9%).
//   3. Grain       — faint per-cell grit (+-3%), only visible up close.
//   4. Occlusion   — the floor darkens toward the walls over a few cells, so
//                    open caves read lighter in the middle and every wall sits
//                    on a dark crease. The caller adds the 1-cell contact
//                    shadow and the wall's rim light on top.
//
// Requires auxTex / s1 and coverageTex / s2. Uses no derivatives, so it is
// safe inside flow control.
// ============================================================================

static const float3 kTunnelFloorNeutral  = float3(0.128, 0.114, 0.100);
static const float3 kTunnelFloorStone    = float3(0.120, 0.120, 0.127);
static const float3 kTunnelFloorConcrete = float3(0.138, 0.138, 0.143);
static const float3 kTunnelFloorDirt     = float3(0.150, 0.111, 0.077);

static const float kTunnelFloorMottle = 0.09;
static const float kTunnelFloorGrain = 0.03;
static const float kTunnelFloorOcclusionRadius = 2.5;   // cells
static const float kTunnelFloorOcclusion = 0.35;        // darkening when fully walled in

// Fraction of solid ground in a ring around a point (0 open cave .. 1 walled in).
float TunnelFloorOcclusion(float2 worldCell)
{
    static const float kDiag = 0.70710678;
    static const float2 kRing[8] =
    {
        float2(1.0, 0.0), float2(-1.0, 0.0), float2(0.0, 1.0), float2(0.0, -1.0),
        float2(kDiag, kDiag), float2(-kDiag, kDiag), float2(kDiag, -kDiag), float2(-kDiag, -kDiag),
    };
    float solid = 0.0;
    [unroll]
    for (int i = 0; i < 8; i++)
        solid += auxTex.SampleLevel(s1, (worldCell + kRing[i] * kTunnelFloorOcclusionRadius) / WorldSize, 0.0).g;
    return solid / 8.0;
}

float3 TunnelFloorColor(float2 worldCell)
{
    // ---- 1. Tint from the surrounding rock -------------------------------
    float3 cover = coverageTex.SampleLevel(s2, worldCell / WorldSize, 0.0).rgb;
    float coverTotal = cover.r + cover.g + cover.b;
    float3 tinted = coverTotal > 1e-4
        ? (cover.r * kTunnelFloorStone + cover.g * kTunnelFloorConcrete + cover.b * kTunnelFloorDirt) / coverTotal
        : kTunnelFloorNeutral;
    float3 floorColor = lerp(kTunnelFloorNeutral, tinted, saturate(coverTotal));

    // ---- 2. Mottle --------------------------------------------------------
    float mottle = fbmNoise(worldCell * 0.055 + float2(613.0, 241.0), 3);
    floorColor *= 1.0 + (mottle - 0.5) * 2.0 * kTunnelFloorMottle;

    // ---- 3. Grain ---------------------------------------------------------
    float grain = valueNoise(worldCell * 0.85 + float2(37.0, 911.0));
    floorColor *= 1.0 + (grain - 0.5) * 2.0 * kTunnelFloorGrain;

    // ---- 4. Occlusion toward the walls ----------------------------------
    float occlusion = smoothstep(0.0, 0.6, TunnelFloorOcclusion(worldCell));
    floorColor *= 1.0 - kTunnelFloorOcclusion * occlusion;

    return floorColor;
}
