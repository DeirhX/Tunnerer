// ============================================================================
// Passes/TerrainAuxPass.hlsli — SDF shading, material overlays, heat & scorch
// ============================================================================
//
// The most complex post-process pass. Uses the auxiliary terrain texture
// (auxTex) to add depth, material identity, and thermal effects to the
// flat-colored terrain rendered by TerrainPs.
//
// Sub-passes executed in order:
//   1. SDF edge shading — Sobel gradient of auxTex.g produces edge strength
//      and boundary proximity. Solid terrain gets a brightness lift and rim
//      highlight near the cave/solid boundary.
//   2. Material classification — auxTex.b encodes material ID (dirt, stone,
//      gold, base). Each material gets its own procedural overlay function.
//   3. Neighborhood AA — 8-tap average blended in at edges to soften
//      remaining aliasing after material overlays.
//   4. Heat & scorch glow — auxTex.r (heat) and auxTex.a (scorch) drive
//      emissive overlays with multi-scale thermal blur.
//   5. Heat debug overlay — optional half-transparent temperature colormap.
//
// All terrain-only effects are gated by `terrainFactor` to avoid bleeding
// onto entity pixels.
// ============================================================================

// ----------------------------------------------------------------------------
// Heat & scorch emissive sub-pass
// ----------------------------------------------------------------------------
// Reads heat (.r) and scorch (.a) from the 3x3 aux neighborhood to produce
// a spatially smooth thermal glow. A wider 2-cell blur ring is blended in
// where steep heat gradients exist, preventing hard contour rings at
// temperature boundaries.
// ----------------------------------------------------------------------------

void ApplyTerrainHeatAndScorch(
    inout float3 color, float2 worldCell, float2 auxUv, float2 mTexel,
    float terrainFactor, float baseInfluence, float4 a0,
    float4 ax1, float4 ax2, float4 ay1, float4 ay2,
    float4 ad1, float4 ad2, float4 ad3, float4 ad4)
{
    float heatMask = terrainFactor * (1.0 - baseInfluence);
    if (heatMask <= 0.001)
        return;

    // Keep heat/scorch accumulation on the same SDF side as the center sample.
    // This prevents explosion scorch outside a wall from bleeding into enclosed
    // interior regions (e.g. base rooms) via neighborhood filtering.
    float sideC = step(0.5, a0.g);
    float sX1 = 1.0 - abs(sideC - step(0.5, ax1.g));
    float sX2 = 1.0 - abs(sideC - step(0.5, ax2.g));
    float sY1 = 1.0 - abs(sideC - step(0.5, ay1.g));
    float sY2 = 1.0 - abs(sideC - step(0.5, ay2.g));
    float sD1 = 1.0 - abs(sideC - step(0.5, ad1.g));
    float sD2 = 1.0 - abs(sideC - step(0.5, ad2.g));
    float sD3 = 1.0 - abs(sideC - step(0.5, ad3.g));
    float sD4 = 1.0 - abs(sideC - step(0.5, ad4.g));

    // ---- Multi-scale heat + scorch blur ------------------------------------
    // Local: 3x3 weighted average (center-heavy), side-aware.
    float hwC = 0.30;
    float hwX1 = 0.10 * sX1; float hwX2 = 0.10 * sX2;
    float hwY1 = 0.10 * sY1; float hwY2 = 0.10 * sY2;
    float hwD1 = 0.05 * sD1; float hwD2 = 0.05 * sD2;
    float hwD3 = 0.05 * sD3; float hwD4 = 0.05 * sD4;
    float hwSum = hwC + hwX1 + hwX2 + hwY1 + hwY2 + hwD1 + hwD2 + hwD3 + hwD4;
    hwSum = max(1e-4, hwSum);

    float heatLocal = (
        a0.r * hwC +
        ax1.r * hwX1 + ax2.r * hwX2 +
        ay1.r * hwY1 + ay2.r * hwY2 +
        ad1.r * hwD1 + ad2.r * hwD2 + ad3.r * hwD3 + ad4.r * hwD4) / hwSum;

    float swC = 0.50;
    float swX1 = 0.10 * sX1; float swX2 = 0.10 * sX2;
    float swY1 = 0.10 * sY1; float swY2 = 0.10 * sY2;
    float swD1 = 0.025 * sD1; float swD2 = 0.025 * sD2;
    float swD3 = 0.025 * sD3; float swD4 = 0.025 * sD4;
    float swSum = swC + swX1 + swX2 + swY1 + swY2 + swD1 + swD2 + swD3 + swD4;
    swSum = max(1e-4, swSum);

    float scorchLocal = (
        a0.a * swC +
        ax1.a * swX1 + ax2.a * swX2 +
        ay1.a * swY1 + ay2.a * swY2 +
        ad1.a * swD1 + ad2.a * swD2 + ad3.a * swD3 + ad4.a * swD4) / swSum;

    // Wide: 2-cell axis samples for smoother falloff (shared fetches).
    float2 mTexel2 = mTexel * 2.0;
    float4 wide0 = auxTex.Sample(s0, auxUv + float2(mTexel2.x, 0.0));
    float4 wide1 = auxTex.Sample(s0, auxUv - float2(mTexel2.x, 0.0));
    float4 wide2 = auxTex.Sample(s0, auxUv + float2(0.0, mTexel2.y));
    float4 wide3 = auxTex.Sample(s0, auxUv - float2(0.0, mTexel2.y));

    float sW0 = 1.0 - abs(sideC - step(0.5, wide0.g));
    float sW1 = 1.0 - abs(sideC - step(0.5, wide1.g));
    float sW2 = 1.0 - abs(sideC - step(0.5, wide2.g));
    float sW3 = 1.0 - abs(sideC - step(0.5, wide3.g));
    float wSum = max(1e-4, sW0 + sW1 + sW2 + sW3);
    float heatWide = (wide0.r * sW0 + wide1.r * sW1 + wide2.r * sW2 + wide3.r * sW3) / wSum;
    float scorchWide = (wide0.a * sW0 + wide1.a * sW1 + wide2.a * sW2 + wide3.a * sW3) / wSum;

    // Blend toward the wider kernel where gradients are steep.
    float heatGrad = abs(ax1.r - ax2.r) + abs(ay1.r - ay2.r);
    float heatEdge = saturate(heatGrad * 1.80);
    float heatBlend = lerp(0.35, 0.75, heatEdge);
    float heat = lerp(heatLocal, heatWide, heatBlend);

    float scorchGrad = abs(ax1.a - ax2.a) + abs(ay1.a - ay2.a);
    float scorchEdge = saturate(scorchGrad * 1.80);
    float scorchBlend = lerp(0.50, 0.80, scorchEdge);
    float scorchSmooth = lerp(scorchLocal, scorchWide, scorchBlend);
    // Scorch support/stability mask: suppress isolated one-pixel remnants that
    // tend to shimmer as they decay near threshold.
    float scorchPresence = (
        step(0.05, a0.a) * swC +
        step(0.05, ax1.a) * swX1 + step(0.05, ax2.a) * swX2 +
        step(0.05, ay1.a) * swY1 + step(0.05, ay2.a) * swY2 +
        step(0.05, ad1.a) * swD1 + step(0.05, ad2.a) * swD2 +
        step(0.05, ad3.a) * swD3 + step(0.05, ad4.a) * swD4) / swSum;
    float scorchStability = smoothstep(0.28, 0.58, scorchPresence);

    // ---- Scorch styling: ash bed + ember clusters ---------------------------
    // Replace noisy residue with a stable "coals among ashes" look.
    static const float3 kAshTintColor = float3(0.15, 0.12, 0.10);
    static const float3 kEmberColorOuter = float3(1.00, 0.42, 0.10);
    static const float3 kEmberColorCore  = float3(1.00, 0.84, 0.30);

    float scorchBody = smoothstep(0.05, 0.20, scorchSmooth) * scorchStability;
    float ashMask = scorchBody * heatMask;
    float ashDarken = lerp(0.88, ScorchTint.y, scorchBody);
    color *= lerp(1.0, ashDarken, ashMask);
    color = lerp(color, kAshTintColor, ashMask * (ScorchTint.z * 0.85));

    // ---- Anti-aliased heat onset -------------------------------------------
    float edgeSoft = max(0.008, fwidth(heat) * (4.0 + 4.0 * heatEdge));
    float fadeIn = smoothstep(0.002 - edgeSoft, 0.09 + edgeSoft, heat);
    float heatNorm = heat * fadeIn;

    // ---- Heat color ramp and intensity ------------------------------------
    float t = pow(saturate(heatNorm), lerp(0.62, 0.86, heatEdge));
    float3 glow = HeatRamp(t);
    float heatGain = lerp(2.00, 2.35, t) * lerp(1.0, 0.92, heatEdge);
    float visible = smoothstep(0.0, lerp(0.040, 0.085, heatEdge), heatNorm);
    color += glow * heatGain * visible * heatMask;

    // Broad char glow (stable, low-frequency).
    float scorchEmissive = smoothstep(0.07, 0.22, scorchSmooth) * scorchSmooth * scorchStability;
    color += MaterialEmissiveScorched.rgb * (scorchEmissive * MaterialEmissiveScorched.a * heatMask * 0.82);

    // Irregular ember clusters: jittered centers, per-cell orientation/anisotropy,
    // and a secondary scale layer to break visible "boxy" footprints.
    float2 emberGrid = worldCell * 0.52;
    float2 emberCell = floor(emberGrid);
    float2 emberLocal = frac(emberGrid) - 0.5;
    float emberHash = frac(sin(dot(emberCell, float2(127.1, 311.7))) * 43758.5453);
    float emberHash2 = frac(sin(dot(emberCell, float2(269.5, 183.3))) * 43758.5453);
    float emberHash3 = frac(sin(dot(emberCell, float2(419.2, 371.9))) * 43758.5453);
    float emberHash4 = frac(sin(dot(emberCell, float2(173.7, 541.8))) * 43758.5453);
    float emberHash5 = frac(sin(dot(emberCell, float2(83.4, 257.6))) * 43758.5453);

    float2 emberJitter = (float2(emberHash2, emberHash3) - 0.5) * 0.44;
    float2 emberDelta = emberLocal - emberJitter;
    float emberAngle = emberHash4 * 6.2831853;
    float sinA = sin(emberAngle);
    float cosA = cos(emberAngle);
    float2 emberRot = float2(
        emberDelta.x * cosA - emberDelta.y * sinA,
        emberDelta.x * sinA + emberDelta.y * cosA);
    float2 emberAniso = float2(lerp(0.72, 1.36, emberHash5), lerp(1.30, 0.74, emberHash5));
    float emberShapeMain = 1.0 - smoothstep(0.12, 0.50, length(emberRot * emberAniso));
    float emberShapeCore = 1.0 - smoothstep(0.06, 0.26, length(emberRot * (emberAniso * 1.2)));
    float emberCluster = smoothstep(0.45, 0.87, emberHash);

    // Secondary micro-coal layer with an offset scale for natural variation.
    float2 emberGridB = worldCell * 0.90 + float2(19.3, 7.1);
    float2 emberCellB = floor(emberGridB);
    float2 emberLocalB = frac(emberGridB) - 0.5;
    float emberHashB = frac(sin(dot(emberCellB, float2(61.7, 149.1))) * 43758.5453);
    float emberHashB2 = frac(sin(dot(emberCellB, float2(211.3, 97.9))) * 43758.5453);
    float2 emberJitterB = (float2(emberHashB2, emberHashB) - 0.5) * 0.32;
    float emberMicro = (1.0 - smoothstep(0.05, 0.20, length(emberLocalB - emberJitterB))) * smoothstep(0.80, 0.98, emberHashB);

    float emberMask = saturate(emberCluster * (emberShapeMain * 0.72 + emberShapeCore * 0.58) + emberMicro * 0.52);
    float emberPhase = (emberHash2 * 0.7 + emberHashB2 * 0.3) * 6.2831853;
    float emberPulse = 0.95 + 0.05 * sin(Time * 0.42 + emberPhase);
    float emberBase = smoothstep(0.10, 0.44, scorchSmooth) * scorchStability * heatMask;
    float ember = emberBase * emberMask * emberPulse;
    color += kEmberColorOuter * (ember * 1.05);
    color += kEmberColorCore * (pow(saturate(ember), 1.35) * 0.62);
}

// ============================================================================
// Main terrain aux pass entry point
// ============================================================================

void ApplyTerrainAuxPass(float2 uv, float terrainFactor, bool textureEnabled, bool heatEnabled, bool thermalRegionsEnabled, inout float3 color)
{
    if (UseTerrainAux <= 0.5 || PixelScale <= 0.0 || (!textureEnabled && !heatEnabled && !thermalRegionsEnabled))
        return;
    bool terrainAuxOnlyMode =
        textureEnabled &&
        !heatEnabled &&
        PostTerrainCurveEnabled <= 0.5 &&
        PostVignetteEnabled <= 0.5 &&
        PostTankGlowEnabled <= 0.5;

    // ---- Coordinate setup & 3x3 aux neighborhood -------------------------
    float2 screenPx = uv * ViewSize;
    float2 worldCell = (CameraPixels + screenPx) / PixelScale;
    float2 auxUv = worldCell / WorldSize;
    float2 mTexel = float2(1.0 / WorldSize.x, 1.0 / WorldSize.y);

    float4 a0  = auxTex.Sample(s0, auxUv);
    float4 ax1 = auxTex.Sample(s0, auxUv + float2( mTexel.x,  0.0));
    float4 ax2 = auxTex.Sample(s0, auxUv + float2(-mTexel.x,  0.0));
    float4 ay1 = auxTex.Sample(s0, auxUv + float2( 0.0,  mTexel.y));
    float4 ay2 = auxTex.Sample(s0, auxUv + float2( 0.0, -mTexel.y));
    float4 ad1 = auxTex.Sample(s0, auxUv + float2( mTexel.x,  mTexel.y));
    float4 ad2 = auxTex.Sample(s0, auxUv + float2( mTexel.x, -mTexel.y));
    float4 ad3 = auxTex.Sample(s0, auxUv + float2(-mTexel.x,  mTexel.y));
    float4 ad4 = auxTex.Sample(s0, auxUv + float2(-mTexel.x, -mTexel.y));

    // ---- Material sampling (needed for base exclusion and texturing) -------
    int2 auxMax = int2(max(1.0, WorldSize.x), max(1.0, WorldSize.y)) - int2(1, 1);
    int2 auxCell = clamp(int2(floor(worldCell)), int2(0, 0), auxMax);
    float4 aNearest = auxTex.Load(int3(auxCell, 0));
    float sdfNearest = aNearest.g;
    float solidCoreHard = step(0.52, sdfNearest);
    float solidCoreSoft = smoothstep(0.52, 0.66, a0.g);
    float solidCore = solidCoreHard * solidCoreSoft;
    float materialCodeNearest = aNearest.b * kMaterialCodeScale;
    // Base pixels now use the concrete material code; no more special base masking.
    float baseInfluence = 0.0;
    // When scorch remnants are present around edges, prefer smoothing and avoid
    // hard contour accents that make the boundary look jagged.
    float scorchNeighbor = max(
        max(max(a0.a, ax1.a), max(ax2.a, ay1.a)),
        max(max(ay2.a, ad1.a), max(ad2.a, max(ad3.a, ad4.a))));
    float scorchEdgeSoften = smoothstep(0.03, 0.14, scorchNeighbor) * (1.0 - baseInfluence);

    // ---- SDF edge shading -------------------------------------------------
    // Sobel gradient of the smooth SDF for edge detection and normal estimation.
    float m0  = a0.g;
    float mx1 = ax1.g; float mx2 = ax2.g;
    float my1 = ay1.g; float my2 = ay2.g;

    float gx = (ad2.g + 2.0 * mx1 + ad1.g) - (ad4.g + 2.0 * mx2 + ad3.g);
    float gy = (ad3.g + 2.0 * my1 + ad1.g) - (ad4.g + 2.0 * my2 + ad2.g);
    float edge = length(float2(gx, gy)) * 0.25;
    float edgeAmt = min(1.0, edge * TerrainMaskEdgeStrength);

    // Boundary proximity: 0 deep inside, 1 at the 0.5 isoline.
    float m = lerp(m0, (mx1 + mx2 + my1 + my2) * 0.25, 0.15);
    float boundary = 1.0 - abs(m * 2.0 - 1.0);
    float outline = min(1.0, boundary * TerrainMaskBoundaryScale);
    float maskWidth = max(fwidth(m) * 1.4, 0.02);
    float maskSoft = smoothstep(0.5 - maskWidth, 0.5 + maskWidth, m);
    // Keep texture shaping on the solid side to avoid cave-side halos.
    float solidSide = smoothstep(0.5 + maskWidth * 0.2, 0.5 + maskWidth * 1.8, m);
    // Suppress contour emphasis on tiny isolated fragments: if the local 3x3
    // neighborhood has little same-side support, treat it as alias/noise and
    // damp edge/rim overlays that would otherwise create a dark halo.
    float sideC = step(0.5, m0);
    float sideSupport = (
        sideC * 2.0 +
        (1.0 - abs(sideC - step(0.5, mx1))) +
        (1.0 - abs(sideC - step(0.5, mx2))) +
        (1.0 - abs(sideC - step(0.5, my1))) +
        (1.0 - abs(sideC - step(0.5, my2))) +
        (1.0 - abs(sideC - step(0.5, ad1.g))) * 0.5 +
        (1.0 - abs(sideC - step(0.5, ad2.g))) * 0.5 +
        (1.0 - abs(sideC - step(0.5, ad3.g))) * 0.5 +
        (1.0 - abs(sideC - step(0.5, ad4.g))) * 0.5) / 8.0;
    float contourSupport = smoothstep(0.48, 0.78, sideSupport);
    // Additional smoothing signal for cave-mouth transitions where boundary
    // energy is high but same-side support is weak (staircase-prone zone).
    float caveTransitionSoften = smoothstep(0.42, 0.86, boundary) * (1.0 - contourSupport) * (1.0 - baseInfluence) * solidSide;

    if (textureEnabled)
    {
        // Solid-side brightness lift and rim highlight near the boundary.
        // In TerrainAux-only mode, suppress ring-like edge accents to avoid the
        // visible outline artifact around hard silhouettes.
        float edgeProfile = edgeAmt * smoothstep(0.05, 0.8, boundary) * (1.0 - baseInfluence) * solidSide * solidCore * contourSupport;
        edgeProfile *= (1.0 - 0.90 * scorchEdgeSoften);
        edgeProfile *= (1.0 - 0.55 * caveTransitionSoften);
        float solidLift = terrainAuxOnlyMode ? TerrainMaskSolidLift * 0.35 : TerrainMaskSolidLift;
        float rimLift = terrainAuxOnlyMode ? 0.0 : TerrainMaskRimLift;
        color += maskSoft * edgeProfile * solidLift * terrainFactor;
        color += maskSoft * edgeProfile * outline * rimLift * terrainFactor;

        // ---- Material classification --------------------------------------
        // Use nearest IDs in stable interiors, but blend toward the bilinear
        // material sample around boundaries to avoid re-introducing staircase.
        float materialCodeSmooth = a0.b * kMaterialCodeScale;
        float materialBoundary = abs(ax1.b - ax2.b) + abs(ay1.b - ay2.b);
        float materialBlend = smoothstep(0.01, 0.08, materialBoundary);
        float materialCode = lerp(materialCodeNearest, materialCodeSmooth, materialBlend);

        float contentMask = terrainFactor * solidSide * solidCore;
        float dirtMask     = (1.0 - smoothstep(0.0, kMaterialMaskWidthWide,   abs(materialCode - kMaterialCodeDirt)))     * contentMask;
        float concreteMask = (1.0 - smoothstep(0.0, kMaterialMaskWidthWide,   abs(materialCode - kMaterialCodeConcrete))) * contentMask;
        float stoneMask    = (1.0 - smoothstep(0.0, kMaterialMaskWidthWide,   abs(materialCode - kMaterialCodeStone)))    * contentMask;
        float goldMask     = (1.0 - smoothstep(0.0, kMaterialMaskWidthNarrow, abs(materialCode - kMaterialCodeGold)))     * contentMask;

        dirtMask = saturate(dirtMask * (1.0 - stoneMask * 0.6) * (1.0 - goldMask) * (1.0 - concreteMask));

        // ---- Procedural material overlays ---------------------------------
        float3 lightDir = normalize(float3(LightDir.xy, max(0.35, LightDir.z)));
        ApplyStoneMaterial(color, worldCell, stoneMask, lightDir);
        ApplyConcreteMaterial(color, worldCell, concreteMask, lightDir);
        ApplyDirtMaterial(color, worldCell, dirtMask, lightDir);
        ApplyGoldMaterial(color, worldCell, goldMask, lightDir);

        // ---- Neighborhood AA ----------------------------------------------
        // Blend a lightweight 8-tap average at terrain edges to soften residual
        // aliasing from the material overlays.
        // Smaller AA radius in TerrainAux-only mode to avoid perceived boundary
        // displacement while still softening jagged cell transitions.
        float aaRadiusScale = terrainAuxOnlyMode ? 0.55 : 1.0;
        float2 oneCell = PixelScale * TexelSize * aaRadiusScale;
        float4 aaC = sceneTex.Sample(s0, uv);
        float4 aaX1 = sceneTex.Sample(s0, uv + float2( oneCell.x,  0.0));
        float4 aaX2 = sceneTex.Sample(s0, uv + float2(-oneCell.x,  0.0));
        float4 aaY1 = sceneTex.Sample(s0, uv + float2( 0.0,  oneCell.y));
        float4 aaY2 = sceneTex.Sample(s0, uv + float2( 0.0, -oneCell.y));
        float4 aaD1 = sceneTex.Sample(s0, uv + float2( oneCell.x,  oneCell.y));
        float4 aaD2 = sceneTex.Sample(s0, uv + float2( oneCell.x, -oneCell.y));
        float4 aaD3 = sceneTex.Sample(s0, uv + float2(-oneCell.x,  oneCell.y));
        float4 aaD4 = sceneTex.Sample(s0, uv + float2(-oneCell.x, -oneCell.y));

        // Prevent AA from blending across cave/solid boundary (main halo source).
        float sideX1 = 1.0 - abs(sideC - step(0.5, ax1.g));
        float sideX2 = 1.0 - abs(sideC - step(0.5, ax2.g));
        float sideY1 = 1.0 - abs(sideC - step(0.5, ay1.g));
        float sideY2 = 1.0 - abs(sideC - step(0.5, ay2.g));
        float sideD1 = 1.0 - abs(sideC - step(0.5, ad1.g));
        float sideD2 = 1.0 - abs(sideC - step(0.5, ad2.g));
        float sideD3 = 1.0 - abs(sideC - step(0.5, ad3.g));
        float sideD4 = 1.0 - abs(sideC - step(0.5, ad4.g));

        float wC = step(kTerrainAlphaThreshold, aaC.a) * 2.0;
        float wX1 = step(kTerrainAlphaThreshold, aaX1.a) * sideX1;
        float wX2 = step(kTerrainAlphaThreshold, aaX2.a) * sideX2;
        float wY1 = step(kTerrainAlphaThreshold, aaY1.a) * sideY1;
        float wY2 = step(kTerrainAlphaThreshold, aaY2.a) * sideY2;
        float wD1 = step(kTerrainAlphaThreshold, aaD1.a) * sideD1 * 0.8;
        float wD2 = step(kTerrainAlphaThreshold, aaD2.a) * sideD2 * 0.8;
        float wD3 = step(kTerrainAlphaThreshold, aaD3.a) * sideD3 * 0.8;
        float wD4 = step(kTerrainAlphaThreshold, aaD4.a) * sideD4 * 0.8;
        float wSum = wC + wX1 + wX2 + wY1 + wY2 + wD1 + wD2 + wD3 + wD4;
        if (wSum > 0.001)
        {
            float3 aaNeighborhood =
                (aaC.rgb * wC + aaX1.rgb * wX1 + aaX2.rgb * wX2 + aaY1.rgb * wY1 + aaY2.rgb * wY2 +
                aaD1.rgb * wD1 + aaD2.rgb * wD2 + aaD3.rgb * wD3 + aaD4.rgb * wD4) / wSum;
            bool nativeSmoothingOff = NativeContinuousParams.y <= 1e-5;
            float aaBase = nativeSmoothingOff ? 0.62 : 0.30;
            if (terrainAuxOnlyMode)
                aaBase *= 0.55;
            float terrainContent = saturate(dirtMask + stoneMask + concreteMask + goldMask);
            float aaContentBoost = smoothstep(0.25, 0.90, terrainContent) * 0.10;
            float aaEdgeSignal = max(edgeProfile, materialBlend * 0.9) * (1.0 - baseInfluence);
            float aaMix = saturate(aaEdgeSignal * (aaBase + aaContentBoost)) * terrainFactor * contourSupport;
            aaMix = saturate(aaMix + scorchEdgeSoften * 0.26 + caveTransitionSoften * 0.18);
            color = lerp(color, aaNeighborhood, aaMix);
        }
    }

    // ---- Heat & scorch glow -----------------------------------------------
    if (heatEnabled)
    {
        ApplyTerrainHeatAndScorch(color, worldCell, auxUv, mTexel, terrainFactor, baseInfluence,
            a0, ax1, ax2, ay1, ay2, ad1, ad2, ad3, ad4);
    }

    // ---- Heat debug overlay (optional) ------------------------------------
    if (HeatDebugOverlay > 0.5)
    {
        float temperature = a0.r * kHeatDebugTemperatureScale;
        float heat01 = saturate(temperature / 100.0);
        color = lerp(color, HeatDebugRamp(heat01), 0.5);
    }

    if (thermalRegionsEnabled && ThermalTileSizeCells > 0.5)
    {
        float tileSize = max(1.0, ThermalTileSizeCells);
        float2 tileCoord = worldCell / tileSize;
        float2 tileFrac = frac(tileCoord);

        float edgeDist = min(min(tileFrac.x, 1.0 - tileFrac.x), min(tileFrac.y, 1.0 - tileFrac.y));
        float borderMask = 1.0 - smoothstep(0.03, 0.09, edgeDist);
        float fillMask = 1.0 - borderMask;

        float2 tileMinCell = floor(tileCoord) * tileSize;
        float2 tileCenterCell = tileMinCell + tileSize * 0.5;
        float2 tileCenterUv = tileCenterCell / WorldSize;
        float2 tileSampleOffset = float2(tileSize / max(1.0, WorldSize.x), tileSize / max(1.0, WorldSize.y)) * 0.32;

        float h0 = auxTex.Sample(s0, tileCenterUv).r;
        float h1 = auxTex.Sample(s0, tileCenterUv + float2(tileSampleOffset.x, 0.0)).r;
        float h2 = auxTex.Sample(s0, tileCenterUv - float2(tileSampleOffset.x, 0.0)).r;
        float h3 = auxTex.Sample(s0, tileCenterUv + float2(0.0, tileSampleOffset.y)).r;
        float h4 = auxTex.Sample(s0, tileCenterUv - float2(0.0, tileSampleOffset.y)).r;
        float heatProxy = (h0 * 0.40) + ((h1 + h2 + h3 + h4) * 0.15);
        float active = step(ThermalTileHeatThreshold01, heatProxy);

        float3 inactiveTint = float3(35.0 / 255.0, 110.0 / 255.0, 220.0 / 255.0);
        float3 activeTint = float3(40.0 / 255.0, 220.0 / 255.0, 75.0 / 255.0);
        float3 tint = lerp(inactiveTint, activeTint, active);

        float fillStrength = lerp(0.08, 0.18, active);
        float edgeStrength = lerp(0.42, 0.60, active);
        float strength = fillMask * fillStrength + borderMask * edgeStrength;
        color = lerp(color, tint, saturate(strength));
    }
}
