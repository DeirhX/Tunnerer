// ============================================================================
// Materials/ConcreteMaterial.hlsli — Procedural weathered concrete overlay
// ============================================================================
//
// Light blue-gray manufactured surface — distinctly LIGHTER than stone's dark
// angular rubble. Features branching crack networks, aggregate speckle,
// surface pitting, and subtle form-panel variation. The overall brightness
// (0.55-0.78 range) contrasts clearly with stone (0.30-0.55) and dirt (brown).
//
// Layers:
//   1. Smooth body     — low-frequency relief for gentle surface undulation
//   2. Crack networks  — multi-scale branching fractures (the signature look)
//   3. Aggregate       — embedded gravel speckle visible through worn surface
//   4. Pitting/spall   — small weathering holes and chipped patches
//   5. Panel variation — subtle tonal shift between poured sections
//   6. Stain patches   — organic discoloration from water / age
// ============================================================================

void ApplyConcreteMaterial(inout float3 color, float2 worldCell, float concreteMask, float3 lightDir)
{
    if (concreteMask > 0.0)
    {
        // ---- 1. Smooth body surface with normal mapping ------------------------
        float2 cP = worldCell * 0.014;
        float c0 = fbmNoise(cP + float2(53.0, 317.0), 4);
        float cnx = fbmNoise(cP + float2(55.0, 317.0), 4);
        float cny = fbmNoise(cP + float2(53.0, 319.0), 4);
        float2 cGrad = float2(cnx - c0, cny - c0);
        float3 cNormal = normalize(float3(-cGrad * 4.0, 1.0));
        float cDiffuse = dot(cNormal, lightDir) * 0.5 + 0.5;

        // ---- 2. Crack networks — the signature concrete look -------------------
        // Domain warp for crack paths.
        float2 crackWarp = float2(
            fbmNoise(worldCell * 0.025 + float2(411.0, 73.0), 3),
            fbmNoise(worldCell * 0.025 + float2(173.0, 509.0), 3));
        float2 crackP = worldCell * 0.12 + (crackWarp - 0.5) * 0.4;

        // Primary cracks: broad, branching.
        float cr1 = 1.0 - abs(fbmNoise(
            float2(crackP.x * 1.20 + crackP.y * 0.40, -crackP.x * 0.40 + crackP.y * 1.20)
            + float2(307.0, 61.0), 4) * 2.0 - 1.0);
        float cr2 = 1.0 - abs(fbmNoise(
            float2(crackP.x * 0.95 - crackP.y * 0.60, crackP.x * 0.60 + crackP.y * 0.95)
            + float2(613.0, 251.0), 4) * 2.0 - 1.0);
        float cr3 = 1.0 - abs(fbmNoise(crackP * 1.5 + float2(89.0, 733.0), 3) * 2.0 - 1.0);
        float primaryCrack = max(max(cr1, cr2), cr3);
        float primaryMask = smoothstep(0.92, 0.995, primaryCrack);
        primaryMask *= smoothstep(0.20, 0.70, fbmNoise(crackP * 0.4 + float2(509.0, 37.0), 3));

        // Secondary cracks: finer, more numerous.
        float2 fineP = worldCell * 0.28 + (crackWarp - 0.5) * 0.2;
        float fc1 = 1.0 - abs(fbmNoise(
            float2(fineP.x * 1.40 + fineP.y * 0.25, -fineP.x * 0.25 + fineP.y * 1.40)
            + float2(827.0, 149.0), 3) * 2.0 - 1.0);
        float fc2 = 1.0 - abs(fbmNoise(
            float2(fineP.x * 1.10 - fineP.y * 0.50, fineP.x * 0.50 + fineP.y * 1.10)
            + float2(197.0, 883.0), 3) * 2.0 - 1.0);
        float fineCrack = max(fc1, fc2);
        float fineMask = smoothstep(0.94, 0.997, fineCrack);
        fineMask *= smoothstep(0.30, 0.75, fbmNoise(fineP * 0.5 + float2(349.0, 61.0), 2));

        // Hairline surface cracks.
        float hairline = 1.0 - abs(fbmNoise(worldCell * 0.55 + float2(701.0, 281.0), 2) * 2.0 - 1.0);
        float hairMask = smoothstep(0.96, 0.998, hairline) * 0.4;

        // ---- 3. Aggregate — embedded gravel visible through wear ---------------
        float aggSeed = fbmNoise(worldCell * 0.42 + float2(191.0, 73.0), 2);
        float aggMask = smoothstep(0.76, 0.93, aggSeed);
        float aggBright = hash21(floor(worldCell * 0.42) + float2(67.0, 211.0));
        // Aggregate normals for shading.
        float aggNx = fbmNoise(worldCell * 0.42 + float2(193.0, 73.0), 2);
        float aggNy = fbmNoise(worldCell * 0.42 + float2(191.0, 75.0), 2);
        float3 aggNormal = normalize(float3(-(aggNx - aggSeed) * 8.0, -(aggNy - aggSeed) * 8.0, 1.0));
        float aggDiffuse = dot(aggNormal, lightDir) * 0.5 + 0.5;

        // ---- 4. Pitting / spalling — small weathering holes --------------------
        float pitSeed = fbmNoise(worldCell * 0.30 + float2(607.0, 263.0), 3);
        float pitMask = smoothstep(0.82, 0.95, pitSeed);
        float spallSeed = fbmNoise(worldCell * 0.09 + float2(431.0, 157.0), 3);
        float spallMask = smoothstep(0.80, 0.96, spallSeed) * smoothstep(0.35, 0.70, fbmNoise(worldCell * 0.04 + float2(263.0, 811.0), 2));

        // ---- 5. Panel variation — subtle tonal shift per poured section --------
        float2 panelUV = worldCell * 0.025;
        float panelNoise = fbmNoise(panelUV + float2(143.0, 397.0), 2);
        float panelShift = lerp(0.96, 1.04, panelNoise);

        // ---- 6. Stain patches — organic discoloration --------------------------
        float stainSeed = fbmNoise(worldCell * 0.035 + float2(571.0, 89.0), 4);
        float stainMask = smoothstep(0.55, 0.85, stainSeed);

        // ---- Color composition — light blue-gray concrete ----------------------
        float3 concreteBase  = float3(0.62, 0.64, 0.68);
        float3 concreteLight = float3(0.76, 0.77, 0.80);
        float3 concColor = lerp(concreteBase, concreteLight, cDiffuse * 0.65);

        // Body surface variation.
        concColor *= 0.88 + 0.18 * cDiffuse;
        concColor *= panelShift;

        // Stain discoloration — warm brownish patches.
        float3 stainTint = float3(0.58, 0.55, 0.50);
        concColor = lerp(concColor, stainTint, stainMask * 0.15);

        // Aggregate inclusions — slightly darker/lighter gravel dots.
        float3 aggColor = lerp(float3(0.50, 0.48, 0.46), float3(0.70, 0.68, 0.64), aggBright);
        aggColor *= 0.80 + 0.30 * aggDiffuse;
        concColor = lerp(concColor, aggColor, aggMask * 0.40);

        // Pitting — dark holes.
        concColor *= 1.0 - pitMask * 0.18;

        // Spalling — lighter exposed aggregate patches.
        float3 spallColor = float3(0.72, 0.70, 0.66);
        concColor = lerp(concColor, spallColor, spallMask * 0.25);

        // Crack compositing — dark lines.
        float3 crackColor = concColor * 0.35;
        concColor = lerp(concColor, crackColor, primaryMask * 0.80);
        concColor = lerp(concColor, crackColor, fineMask * 0.55);
        concColor *= 1.0 - hairMask * 0.20;

        // High-frequency surface grain.
        float surfGrain = fbmNoise(worldCell * 0.55 + float2(181.0, 73.0), 2);
        concColor *= lerp(0.95, 1.05, surfGrain);

        // ---- Final blend -------------------------------------------------------
        color = lerp(color, concColor, concreteMask * 0.90);
    }
}
