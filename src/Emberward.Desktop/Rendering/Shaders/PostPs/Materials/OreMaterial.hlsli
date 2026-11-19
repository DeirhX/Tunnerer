// ============================================================================
// Materials/OreMaterial.hlsli — Emberite ore deposits
// ============================================================================
//
// Draws emberite from the ore field (see OreField in TerrainAuxPass): x is the
// field of every ore cell (kOreIso is the deposit's edge), y the field of the
// core cells of a lump, z the ore's temperature (the glow dims as it cools).
//
// Layers:
//   1. Seat shadow   — a faint dark rim just outside the deposit
//   2. Crystal body  — gold shading into amber, broken into angular facets
//                      with bright glints, so it reads as crystal rather than
//                      rock (grey) or hot rock (red heat)
//   3. Speck glow    — every ore cell (specks, pockets and lumps alike) is lit
//                      from within, and a soft gold halo spills about a cell
//                      and a half onto the ground around it (the 3x3 cell
//                      neighbourhood the ore field is sampled over)
//   4. Core glow     — a slow warm pulse (pale gold to ember) over core cells
// ============================================================================

void ApplyOreDeposit(inout float3 color, float2 worldCell, float3 field, float mask, float3 lightDir)
{
    float ore = field.x;
    // The glow follows the ore's own temperature: a cooling speck or core dims toward a faint ember.
    float warmth = kOreGlowMin + (1.0 - kOreGlowMin) * saturate(field.z / kOreGlowFullTemperature);
    float aa = max(fwidth(ore), 0.02);
    if (mask > 0.0 && ore > kOreGlowReach)
    {
        float body = smoothstep(kOreIso - aa, kOreIso + aa, ore);
        float depth = smoothstep(kOreIso, kOreIso + 0.9, ore);
        float pulse = 0.75 + 0.25 * sin(Time * 1.7 + (worldCell.x - worldCell.y) * 0.4);

        // ---- 1. Seat shadow ----------------------------------------------------
        float seat = smoothstep(kOreIso - 0.28, kOreIso, ore) * (1.0 - body);
        color *= 1.0 - 0.3 * seat * mask;

        // ---- 2. Crystal body ---------------------------------------------------
        // Facets: Voronoi cells about a cell and a half across, each a flat crystal
        // face with its own tilt and hue, with dark seams between them.
        float2 p = worldCell * 0.65;
        float2 baseCell = floor(p);
        float nearest = 8.0, second = 8.0;
        float2 facetId = baseCell;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                float2 c = baseCell + float2(dx, dy);
                float2 site = c + float2(hash21(c), hash21(c + float2(41.0, 7.0)));
                float d = length(p - site);
                if (d < nearest) { second = nearest; nearest = d; facetId = c; }
                else if (d < second) { second = d; }
            }
        float seam = 1.0 - smoothstep(0.0, 0.12, second - nearest);
        float facetTilt = hash21(facetId + float2(17.0, 91.0)) * 6.2831853;
        float3 facetNormal = normalize(float3(cos(facetTilt) * 0.8, sin(facetTilt) * 0.8, 1.0));
        float facetLight = saturate(dot(facetNormal, lightDir));
        float hue = hash21(facetId + float2(5.0, 3.0));

        float3 amber = float3(0.95, 0.52, 0.14);
        float3 gold = float3(1.0, 0.76, 0.25);
        float3 crystal = lerp(amber, gold, saturate(hue * 1.1 - 0.05 + depth * 0.15));
        crystal *= 0.50 + 0.75 * facetLight;
        crystal += float3(0.75, 1.0, 0.98) * pow(facetLight, 10.0) * 0.6;
        crystal *= 1.0 - 0.55 * seam;

        // ---- 3. Speck glow -----------------------------------------------------
        // Emissive body on every ore cell, then a halo that fades out over the
        // field's tail (about 1.5 cells from a lone cell's centre), added on top
        // so it lights the dirt and the tunnel floor beside the ore.
        crystal += float3(1.0, 0.72, 0.22) * (0.06 + 0.06 * pulse) * warmth;
        float halo = smoothstep(kOreGlowReach, kOreIso, ore) * (1.0 - body);
        color += float3(1.0, 0.68, 0.20) * halo * (0.60 + 0.25 * pulse) * warmth * mask;

        // ---- 4. Core glow ------------------------------------------------------
        float core = smoothstep(0.25, 0.9, field.y);
        float3 ember = lerp(float3(1.0, 0.95, 0.72), float3(1.0, 0.62, 0.25), core);
        core *= warmth;
        crystal = lerp(crystal, ember * (0.7 + 0.5 * facetLight), core * 0.75);
        crystal += ember * core * pulse * 0.35;

        color = lerp(color, crystal, body * mask);
    }
}
