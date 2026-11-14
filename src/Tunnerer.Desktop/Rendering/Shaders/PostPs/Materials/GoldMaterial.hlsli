// ============================================================================
// Materials/GoldMaterial.hlsli — Gold vein overlay
// ============================================================================
//
// Draws a gold vein from the vein field (see GoldVeinField in TerrainAuxPass):
// kGoldVeinIso is the edge of the band, higher values are closer to its spine.
//
// Layers:
//   1. Seat shadow   — darkens the rock just outside the band so it reads as
//                      set into the stone
//   2. Faceted body  — amber at the edge, bright gold along the spine, broken
//                      into small lit / shaded crystal facets
//   3. Emissive      — a slow pulse along the spine
// ============================================================================

void ApplyGoldVein(inout float3 color, float2 worldCell, float field, float mask, float3 lightDir)
{
    float aa = max(fwidth(field), 0.02);
    if (mask <= 0.0 || field <= kGoldVeinIso - 0.35)
        return;

    float body = smoothstep(kGoldVeinIso - aa, kGoldVeinIso + aa, field);
    float spine = smoothstep(kGoldVeinIso + 0.03, kGoldVeinIso + 0.45, field);

    // ---- 1. Seat shadow ----------------------------------------------------
    float seat = smoothstep(kGoldVeinIso - 0.32, kGoldVeinIso, field) * (1.0 - body);
    color *= 1.0 - 0.55 * seat * mask;

    // ---- 2. Faceted body ---------------------------------------------------
    float2 facetCell = floor(worldCell * 2.2);
    float facetTilt = hash21(facetCell + float2(31.0, 157.0)) * 6.2831853;
    float3 facetNormal = normalize(float3(cos(facetTilt) * 0.55, sin(facetTilt) * 0.55, 1.0));
    float facetLight = saturate(dot(facetNormal, lightDir));

    float3 amber = float3(0.66, 0.36, 0.05);
    float3 gold = float3(1.00, 0.76, 0.20);
    float3 vein = lerp(amber, gold, spine) * (0.72 + 0.50 * facetLight);
    vein += float3(1.0, 0.95, 0.70) * pow(facetLight, 6.0) * 0.35 * spine;

    // ---- 3. Emissive pulse -------------------------------------------------
    float pulse = MaterialEmissivePulse.y + MaterialEmissivePulse.z *
        (0.5 + 0.5 * sin(Time * MaterialEmissivePulse.x + (worldCell.x + worldCell.y) * 0.35));
    vein += MaterialEmissiveGold.rgb * (spine * MaterialEmissiveGold.a * pulse * 0.35);

    color = lerp(color, vein, body * mask);
}
