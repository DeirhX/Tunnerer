// ============================================================================
// Materials/StoneMaterial.hlsli — Procedural rubble-pile stone overlay
// ============================================================================
//
// Chunky angular rocks with dark crevices. Each rock is a Voronoi cell with
// strong directional facet shading — bright highlights on light-facing planes,
// deep shadows on the rest. Small pebbles fill gaps between large rocks.
//
// Layer stack:
//   1. Primary Voronoi   — large rock chunks with dark mortar gaps
//   2. Sub-facet Voronoi — angular shading planes within each rock
//   3. Pebble fill       — small rocks in mortar gaps
//   4. Surface relief    — bump-mapped roughness
//   5. Specular edge     — bright highlight on light-facing facet edges
//   6. Macro variation   — broad regional shift
// ============================================================================

void ApplyStoneMaterial(inout float3 color, float2 worldCell, float materialMask, float3 lightDir)
{
    if (materialMask <= 0.0) return;

    // ---- 1. Primary Voronoi — large rock chunks ----------------------------
    // Frequency 0.075 gives ~13 world-cell rocks (~65 screen px).
    float2 bP = worldCell * 0.075;
    float2 bCell = floor(bP);
    float minDist = 8.0;
    float secondDist = 8.0;
    float2 closestCell = bCell;
    for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            float2 neighbor = bCell + float2(dx, dy);
            float2 jitter = float2(hash21(neighbor + float2(0.0, 0.0)),
                                   hash21(neighbor + float2(37.0, 113.0)));
            float2 diff = neighbor + jitter - bP;
            float d = dot(diff, diff);
            if (d < minDist) { secondDist = minDist; minDist = d; closestCell = neighbor; }
            else if (d < secondDist) { secondDist = d; }
        }
    float edgeDist = sqrt(secondDist) - sqrt(minDist);
    float rockHash  = hash21(closestCell + float2(401.0, 59.0));
    float rockHash2 = hash21(closestCell + float2(173.0, 311.0));
    float rockHash3 = hash21(closestCell + float2(89.0, 547.0));

    // Mortar gap with noise-modulated width.
    float mortarNoise = fbmNoise(worldCell * 0.15 + float2(601.0, 47.0), 2);
    float mortarThreshold = lerp(0.05, 0.13, mortarNoise);
    float mortarGap = smoothstep(mortarThreshold, mortarThreshold * 0.12, edgeDist);

    // Edge AO: darken near rock borders.
    float edgeAO = smoothstep(0.03, 0.28, edgeDist);

    // ---- 2. Sub-facet Voronoi — 2-4 flat planes per rock -------------------
    float2 sP = worldCell * 0.18;
    float2 sCell = floor(sP);
    float sMinD = 8.0;
    float sSecD = 8.0;
    float2 sClosest = sCell;
    for (int sy = -1; sy <= 1; sy++)
        for (int sx = -1; sx <= 1; sx++)
        {
            float2 sn = sCell + float2(sx, sy);
            float2 sj = float2(hash21(sn + float2(7.0, 53.0)),
                               hash21(sn + float2(101.0, 29.0)));
            float2 sd = sn + sj - sP;
            float d2 = dot(sd, sd);
            if (d2 < sMinD) { sSecD = sMinD; sMinD = d2; sClosest = sn; }
            else if (d2 < sSecD) { sSecD = d2; }
        }
    // Sub-facet edge: sharp crease between adjacent flat planes.
    float subEdgeDist = sqrt(sSecD) - sqrt(sMinD);
    float subEdge = smoothstep(0.10, 0.03, subEdgeDist);

    float subHash = hash21(sClosest + float2(211.0, 83.0));
    float subHash2 = hash21(sClosest + float2(347.0, 151.0));

    // Sub-facet normal: strong tilt for dramatic light/shadow.
    float subAngle = subHash * 6.2831853;
    float subTilt = lerp(0.35, 0.70, subHash2);
    float3 subNormal = normalize(float3(
        cos(subAngle) * subTilt,
        sin(subAngle) * subTilt,
        1.0));
    float subDiffuse = dot(subNormal, lightDir) * 0.5 + 0.5;

    // Primary rock normal.
    float rockAngle = rockHash * 6.2831853;
    float rockTilt = lerp(0.30, 0.60, rockHash2);
    float3 rockNormal = normalize(float3(
        cos(rockAngle) * rockTilt,
        sin(rockAngle) * rockTilt,
        1.0));
    float rockDiffuse = dot(rockNormal, lightDir) * 0.5 + 0.5;

    // ---- 3. Pebble fill — small rocks between large ones -------------------
    float2 pP = worldCell * 0.28;
    float2 pCell = floor(pP);
    float pMinD = 8.0;
    float2 pClosest = pCell;
    for (int py = -1; py <= 1; py++)
        for (int px = -1; px <= 1; px++)
        {
            float2 pn = pCell + float2(px, py);
            float2 pj = float2(hash21(pn + float2(131.0, 41.0)),
                               hash21(pn + float2(263.0, 97.0)));
            float2 pd = pn + pj - pP;
            float pd2 = dot(pd, pd);
            if (pd2 < pMinD) { pMinD = pd2; pClosest = pn; }
        }
    float pebbleHash = hash21(pClosest + float2(601.0, 173.0));
    float pebbleDiffuse = lerp(0.45, 0.85, pebbleHash);
    float pebbleMask = mortarGap * 0.7;

    // ---- 4. Surface relief — fine roughness --------------------------------
    float2 reliefP = worldCell * 0.14;
    float r0 = fbmNoise(reliefP + float2(17.0, 3.0), 3);
    float rnx = fbmNoise(reliefP + float2(19.0, 3.0), 3);
    float rny = fbmNoise(reliefP + float2(17.0, 5.0), 3);
    float3 reliefNormal = normalize(float3(-(rnx - r0) * 5.0, -(rny - r0) * 5.0, 1.0));
    float reliefDiffuse = dot(reliefNormal, lightDir) * 0.5 + 0.5;

    // ---- 5. Specular edge highlights ---------------------------------------
    // Bright pop where rock facet faces directly toward the light.
    float3 combinedNormal = normalize(rockNormal + subNormal * 0.7);
    float spec = pow(saturate(dot(combinedNormal, lightDir)), 4.0);

    // ---- 6. Macro variation ------------------------------------------------
    float macro = fbmNoise(worldCell * 0.012 + float2(301.0, 133.0), 3);

    // ---- Color composition — high-contrast rubble pile ---------------------
    float3 stoneDark  = float3(0.20, 0.19, 0.18);
    float3 stoneMid   = float3(0.34, 0.32, 0.30);
    float3 stoneLight = float3(0.52, 0.50, 0.46);

    // Per-rock tonal variation: mostly dark, some lighter.
    float3 rockBase = lerp(stoneDark, stoneMid, rockHash3);
    rockBase = lerp(rockBase, stoneLight, saturate(rockHash3 * 0.4 + rockDiffuse * 0.3));
    float rockBright = lerp(0.60, 1.15, rockDiffuse);

    // Sub-facet shading: dramatic within-rock contrast (this is the main 3D cue).
    float facetBright = lerp(0.50, 1.50, subDiffuse);
    float3 stoneColor = rockBase * rockBright * facetBright;

    // Surface roughness.
    stoneColor *= lerp(0.90, 1.10, reliefDiffuse);

    // Edge ambient occlusion: darken near rock borders.
    stoneColor *= lerp(0.60, 1.0, edgeAO);

    // Sub-facet edge crease: sharp dark line between flat planes within a rock.
    stoneColor *= 1.0 - subEdge * 0.30 * (1.0 - mortarGap);

    // Specular highlight: concentrated bright pop on light-facing facets.
    stoneColor += float3(0.12, 0.11, 0.10) * spec * (1.0 - mortarGap) * (1.0 - subEdge) * materialMask;

    // Macro variation.
    stoneColor *= lerp(0.90, 1.10, macro);

    // Chip dust.
    float chipDust = fbmNoise(worldCell * 0.52 + float2(181.0, 73.0), 2);
    stoneColor *= lerp(0.96, 1.04, chipDust);

    // Pebbles in mortar gaps.
    float3 pebbleColor = stoneMid * pebbleDiffuse;
    stoneColor = lerp(stoneColor, pebbleColor, pebbleMask * 0.50);

    // Mortar: very dark crevices.
    float3 mortarColor = float3(0.08, 0.07, 0.06);
    stoneColor = lerp(stoneColor, mortarColor, mortarGap * 0.88);

    // Cap brightness — reference is mostly dark with occasional highlights.
    stoneColor = min(stoneColor, float3(0.58, 0.56, 0.52));

    color = lerp(color, stoneColor, materialMask);
}
