// ============================================================================
// Materials/OreMaterial.hlsli — Emberite ore deposits
// ============================================================================
//
// Draws emberite from the ore field (see OreField in TerrainAuxPass): x is the
// field of every ore cell (kOreIso is the deposit's edge), y the field of the
// core cells of a lump.
//
// Layers:
//   1. Seat shadow   — a dark rim just outside the deposit
//   2. Crystal body  — teal shading into violet, broken into angular facets
//                      with bright glints, so it reads as crystal rather than
//                      rock (grey) or hot rock (red heat)
//   3. Core glow     — a slow warm pulse (magenta to ember) over core cells
// ============================================================================

void ApplyOreDeposit(inout float3 color, float2 worldCell, float2 field, float mask, float3 lightDir)
{
    float ore = field.x;
    float aa = max(fwidth(ore), 0.02);
    if (mask <= 0.0 || ore <= kOreIso - 0.30)
        return;

    float body = smoothstep(kOreIso - aa, kOreIso + aa, ore);
    float depth = smoothstep(kOreIso, kOreIso + 0.9, ore);

    // ---- 1. Seat shadow ----------------------------------------------------
    float seat = smoothstep(kOreIso - 0.28, kOreIso, ore) * (1.0 - body);
    color *= 1.0 - 0.6 * seat * mask;

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

    float3 teal = float3(0.12, 0.66, 0.62);
    float3 violet = float3(0.50, 0.26, 0.78);
    float3 crystal = lerp(teal, violet, saturate(hue * 1.1 - 0.05 + depth * 0.15));
    crystal *= 0.50 + 0.75 * facetLight;
    crystal += float3(0.75, 1.0, 0.98) * pow(facetLight, 10.0) * 0.6;
    crystal *= 1.0 - 0.55 * seam;

    // ---- 3. Core glow ------------------------------------------------------
    float core = smoothstep(0.25, 0.9, field.y);
    float pulse = 0.75 + 0.25 * sin(Time * 1.7 + (worldCell.x - worldCell.y) * 0.4);
    float3 ember = lerp(float3(0.95, 0.30, 0.55), float3(1.0, 0.62, 0.25), core);
    crystal = lerp(crystal, ember * (0.7 + 0.5 * facetLight), core * 0.75);
    crystal += ember * core * pulse * 0.35;

    color = lerp(color, crystal, body * mask);
}
