// ============================================================================
// Materials/DirtMaterial.hlsli — Procedural dirt overlay
// ============================================================================
//
// Dark-brown organic soil with visible embedded pebbles and root-like
// structures. Palette is significantly darker than stone/concrete to keep
// the three materials instantly distinguishable.
//
// Layer stack:
//   1. Domain warping      — breaks grid alignment
//   2. Base relief         — broad bumpy surface with normal-mapped diffuse
//   3. Macro landform      — hills, cliff bands, plateaus
//   4. Clump / moisture    — organic wet/dry patches
//   5. Micro detail        — dust, ridges, grain
//   6. Fissures & cracks   — irregular fracture networks
//   7. Pebbles             — clearly visible mineral inclusions
//   8. Clod rims & sparkle — subtle edge highlights
// ============================================================================

void ApplyDirtMaterial(inout float3 color, float2 worldCell, float dirtMask, float3 lightDir)
{
    if (dirtMask > 0.0)
    {
        // ---- 1. Domain warp fields --------------------------------------------
        float2 warp0 = float2(
            fbmNoise(worldCell * 0.020 + float2(43.0, 197.0), 3),
            fbmNoise(worldCell * 0.020 + float2(181.0, 67.0), 3));
        float2 warp1 = float2(
            fbmNoise(worldCell * 0.045 + float2(313.0, 11.0), 2),
            fbmNoise(worldCell * 0.045 + float2(97.0, 419.0), 2));
        float2 dirtP = worldCell * 0.070 + (warp0 - 0.5) * 0.35 + (warp1 - 0.5) * 0.15;

        // ---- 2. Base relief with normal mapping --------------------------------
        float d0 = fbmNoise(dirtP + float2(19.0, 201.0), 4);
        float dnx = fbmNoise(dirtP + float2(21.0, 201.0), 4);
        float dny = fbmNoise(dirtP + float2(19.0, 203.0), 4);
        float2 dGrad = float2(dnx - d0, dny - d0);

        float dg0 = fbmNoise(dirtP * 2.6 + float2(503.0, 71.0), 3);
        float dgnx = fbmNoise(dirtP * 2.6 + float2(505.0, 71.0), 3);
        float dgny = fbmNoise(dirtP * 2.6 + float2(503.0, 73.0), 3);
        float2 dgGrad = float2(dgnx - dg0, dgny - dg0);

        float3 dirtNormal = normalize(float3(-(dGrad * 6.0 + dgGrad * 2.2), 1.0));
        float dirtDiffuse = dot(dirtNormal, lightDir) * 0.5 + 0.5;

        // ---- 3. Macro landform structure ---------------------------------------
        float land = SampleLandformHeight(worldCell + float2(91.0, 47.0));
        float landX = SampleLandformHeight(worldCell + float2(95.0, 47.0));
        float landY = SampleLandformHeight(worldCell + float2(91.0, 51.0));
        float2 landGrad = float2(landX - land, landY - land);
        float slope = length(landGrad);
        float dirtCliff = smoothstep(0.016, 0.046, slope);
        float dirtPlateau = smoothstep(0.63, 0.88, land);
        float dirtHill = smoothstep(0.30, 0.66, land) * (1.0 - dirtPlateau);

        // ---- 4. Clump / moisture / pore fields ---------------------------------
        float clump = smoothstep(0.18, 0.86, d0);
        float moisture = fbmNoise(worldCell * 0.016 + float2(311.0, 97.0), 4);
        float pore = smoothstep(0.64, 0.14, fbmNoise(worldCell * 0.155 + float2(71.0, 13.0), 3));
        float looseDust = fbmNoise(worldCell * 0.105 + float2(151.0, 39.0), 3);
        float grain = fbmNoise(worldCell * 0.290 + float2(433.0, 29.0), 2);
        float grit = fbmNoise(worldCell * 0.470 + float2(263.0, 149.0), 2);

        // ---- 5. Fissures (medium + macro) --------------------------------------
        float2 cP = worldCell * 0.19 + (warp0 - 0.5) * 0.2;
        float c1 = 1.0 - abs(fbmNoise(float2(cP.x * 1.75 + cP.y * 0.33, -cP.x * 0.33 + cP.y * 1.75) + float2(701.0, 19.0), 3) * 2.0 - 1.0);
        float c2 = 1.0 - abs(fbmNoise(float2(cP.x * 1.31 - cP.y * 0.58, cP.x * 0.58 + cP.y * 1.31) + float2(211.0, 907.0), 3) * 2.0 - 1.0);
        float c3 = 1.0 - abs(fbmNoise(cP * 2.6 + float2(47.0, 613.0), 2) * 2.0 - 1.0);
        float crackField = max(max(c1, c2), c3);
        float crackMask = smoothstep(0.95, 0.997, crackField);
        crackMask *= smoothstep(0.24, 0.80, fbmNoise(cP * 0.9 + float2(611.0, 43.0), 3));

        float2 macroP = worldCell * 0.060 + (warp0 - 0.5) * 0.25;
        float m1 = 1.0 - abs(fbmNoise(float2(macroP.x * 1.08 + macroP.y * 0.20, -macroP.x * 0.20 + macroP.y * 1.08) + float2(131.0, 761.0), 3) * 2.0 - 1.0);
        float m2 = 1.0 - abs(fbmNoise(float2(macroP.x * 0.88 - macroP.y * 0.43, macroP.x * 0.43 + macroP.y * 0.88) + float2(521.0, 227.0), 3) * 2.0 - 1.0);
        float macroFissure = smoothstep(0.965, 0.998, max(m1, m2));
        macroFissure *= smoothstep(0.32, 0.84, fbmNoise(macroP * 0.65 + float2(83.0, 947.0), 3));

        // ---- 6. Pebbles — clearly visible lighter inclusions -------------------
        // Large pebbles: isolated tan/gray stones sitting on the soil.
        float pebbleSeed = fbmNoise(worldCell * 0.32 + float2(91.0, 17.0), 2);
        float pebbleCluster = smoothstep(0.40, 0.75, fbmNoise(worldCell * 0.065 + float2(239.0, 311.0), 3));
        float pebbleMask = smoothstep(0.78, 0.94, pebbleSeed) * pebbleCluster;
        // Pebble bump normals for 3D shading.
        float pSeedNx = fbmNoise(worldCell * 0.32 + float2(93.0, 17.0), 2);
        float pSeedNy = fbmNoise(worldCell * 0.32 + float2(91.0, 19.0), 2);
        float3 pebbleNormal = normalize(float3(-(pSeedNx - pebbleSeed) * 12.0, -(pSeedNy - pebbleSeed) * 12.0, 1.0));
        float pebbleDiffuse = dot(pebbleNormal, lightDir) * 0.5 + 0.5;

        // Small pebbles: finer-grained gravel.
        float smallPebbleSeed = fbmNoise(worldCell * 0.62 + float2(457.0, 109.0), 2);
        float smallPebbleMask = smoothstep(0.82, 0.96, smallPebbleSeed) * pebbleCluster * 0.7;

        // ---- Color composition -------------------------------------------------
        // Darker soil palette than before — richer earth tones.
        float dirtDark = 0.030 + 0.070 * pore + 0.040 * (1.0 - clump) + 0.030 * (1.0 - moisture);
        float3 drySoil  = float3(0.58, 0.38, 0.18);
        float3 wetSoil  = float3(0.28, 0.16, 0.08);
        float3 rustySoil = float3(0.48, 0.24, 0.12);
        float warmMineral = fbmNoise(dirtP * 0.85 + float2(801.0, 53.0), 3);
        float3 dirtTint = lerp(drySoil, wetSoil, saturate(1.0 - moisture * 0.9));
        dirtTint = lerp(dirtTint, rustySoil, smoothstep(0.58, 0.90, warmMineral) * 0.40);

        float3 dirtColor = dirtTint * (0.72 + 0.36 * dirtDiffuse);
        dirtColor *= 1.0 - dirtDark;
        dirtColor *= lerp(0.82, 1.18, looseDust);
        dirtColor *= lerp(0.84, 1.18, grain);
        dirtColor *= lerp(0.90, 1.12, grit);

        // ---- Micro detail layers -----------------------------------------------
        float dapple = fbmNoise(worldCell * 0.22 + float2(149.0, 701.0), 2);
        dirtColor *= lerp(0.92, 1.08, dapple);
        float microSpark = fbmNoise(worldCell * 0.95 + float2(81.0, 403.0), 2);
        dirtColor *= lerp(0.88, 1.14, microSpark);
        float microDustA = fbmNoise(worldCell * 1.35 + float2(607.0, 281.0), 2);
        float microDustB = fbmNoise(float2(
            worldCell.x * 1.05 + worldCell.y * 0.37,
            -worldCell.x * 0.37 + worldCell.y * 1.05) + float2(211.0, 1181.0), 2);
        dirtColor *= lerp(0.88, 1.14, microDustA);
        dirtColor *= lerp(0.90, 1.12, microDustB);

        // Fine-scale ridge cracks.
        float tinyRidge = 1.0 - abs(fbmNoise(float2(
            worldCell.x * 0.72 + worldCell.y * 0.19,
            -worldCell.x * 0.19 + worldCell.y * 0.72) + float2(907.0, 151.0), 2) * 2.0 - 1.0);
        float tinyCrack = smoothstep(0.93, 0.995, tinyRidge) * (0.7 + 0.3 * (1.0 - moisture));
        dirtColor *= 1.0 - tinyCrack * 0.05;

        // Apply fissures and macro features.
        dirtColor *= 1.0 - crackMask * (0.06 + 0.07 * (1.0 - moisture));
        dirtColor *= 1.0 - macroFissure * (0.08 + 0.06 * (1.0 - moisture));
        dirtColor *= 1.0 - dirtCliff * 0.10;
        dirtColor *= 1.0 + dirtHill * 0.08 + dirtPlateau * 0.05;

        // ---- Clod rims ---------------------------------------------------------
        float clodRim = smoothstep(0.56, 0.94, clump) * (1.0 - smoothstep(0.78, 0.98, clump));
        dirtColor *= 1.0 + clodRim * 0.06;

        // ---- Pebble compositing (lighter inclusions on dark soil) --------------
        float3 pebbleColor = float3(0.55, 0.50, 0.42) * (0.70 + 0.40 * pebbleDiffuse);
        pebbleColor *= lerp(0.90, 1.10, fbmNoise(worldCell * 0.48 + float2(331.0, 67.0), 2));
        dirtColor = lerp(dirtColor, pebbleColor, pebbleMask * 0.85);

        float3 smallPebbleColor = float3(0.48, 0.44, 0.38) * (0.75 + 0.35 * dirtDiffuse);
        dirtColor = lerp(dirtColor, smallPebbleColor, smallPebbleMask * 0.70);

        // Mineral sparkle on pebbles.
        float microSpec = smoothstep(0.92, 0.995, fbmNoise(worldCell * 1.25 + float2(937.0, 127.0), 2));
        dirtColor = lerp(dirtColor, dirtColor * float3(1.08, 1.04, 0.96), microSpec * 0.14 * (1.0 - moisture));

        // ---- Final blend -------------------------------------------------------
        color = lerp(color, dirtColor, dirtMask);
    }
}
