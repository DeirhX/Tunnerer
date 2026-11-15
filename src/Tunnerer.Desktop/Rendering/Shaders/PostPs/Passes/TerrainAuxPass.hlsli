// ============================================================================
// Passes/TerrainAuxPass.hlsli — Edge shading, material overlays, heat & scorch
// ============================================================================
//
// The most complex post-process pass. Uses the auxiliary terrain texture
// (auxTex) to add depth, material identity, and thermal effects to the
// flat-colored terrain rendered by TerrainPs.
//
// Sub-passes executed in order:
//   1. Material classification — natural materials (stone, concrete, dirt)
//      are chosen from the blurred coverage texture (coverageTex), never from
//      interpolated material codes. Stone/dirt edges get a contact shadow and
//      bevel. The result is composited over the cave by the shared cave/solid
//      edge (Common/TerrainEdge.hlsli), with a rim light on the solid side and
//      a contact shadow on the cave side. The tank base (wall, floor, door) is
//      drawn pixel-exact from the nearest cell's exact code.
//   2. Heat & scorch glow — auxTex.r (heat) and auxTex.a (scorch) drive
//      emissive overlays with multi-scale thermal blur.
//   3. Heat debug overlay — optional half-transparent temperature colormap.
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

// Soft solid/cave side of a sampled field value (0 cave, 1 solid), for side-aware heat blurring.
float HeatSide(float field)
{
    return smoothstep(0.38, 0.62, field);
}

void ApplyTerrainHeatAndScorch(
    inout float3 color, float2 worldCell, float2 auxUv, float2 mTexel,
    float terrainFactor, float baseInfluence, float edgeCoverage, float4 a0,
    float4 ax1, float4 ax2, float4 ay1, float4 ay2,
    float4 ad1, float4 ad2, float4 ad3, float4 ad4)
{
    float heatMask = terrainFactor * (1.0 - baseInfluence);
    if (heatMask <= 0.001)
        return;

    // Keep heat/scorch accumulation on the same SDF side as the center sample.
    // This prevents explosion scorch outside a wall from bleeding into enclosed
    // interior regions (e.g. base rooms) via neighborhood filtering.
    // The side is soft and the centre uses the shared edge's coverage, so the weights change smoothly across the
    // drawn edge instead of in hard steps that show as seams in the glow.
    float sideC = edgeCoverage;
    float sX1 = 1.0 - abs(sideC - HeatSide(ax1.g));
    float sX2 = 1.0 - abs(sideC - HeatSide(ax2.g));
    float sY1 = 1.0 - abs(sideC - HeatSide(ay1.g));
    float sY2 = 1.0 - abs(sideC - HeatSide(ay2.g));
    float sD1 = 1.0 - abs(sideC - HeatSide(ad1.g));
    float sD2 = 1.0 - abs(sideC - HeatSide(ad2.g));
    float sD3 = 1.0 - abs(sideC - HeatSide(ad3.g));
    float sD4 = 1.0 - abs(sideC - HeatSide(ad4.g));

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
    float4 wide0 = auxTex.Sample(s1, auxUv + float2(mTexel2.x, 0.0));
    float4 wide1 = auxTex.Sample(s1, auxUv - float2(mTexel2.x, 0.0));
    float4 wide2 = auxTex.Sample(s1, auxUv + float2(0.0, mTexel2.y));
    float4 wide3 = auxTex.Sample(s1, auxUv - float2(0.0, mTexel2.y));

    float sW0 = 1.0 - abs(sideC - HeatSide(wide0.g));
    float sW1 = 1.0 - abs(sideC - HeatSide(wide1.g));
    float sW2 = 1.0 - abs(sideC - HeatSide(wide2.g));
    float sW3 = 1.0 - abs(sideC - HeatSide(wide3.g));
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

// ----------------------------------------------------------------------------
// Natural material selection
// ----------------------------------------------------------------------------
// Coverage c (stone, concrete, dirt). For each material the signed margin
// d = c_i - max(other two) is positive where it dominates; a smoothstep of
// d with a screen-space width gives an anti-aliased, smooth-curved boundary.
// Masks are normalised to sum to 1.

float3 CoverageMargins(float3 c)
{
    return float3(
        c.x - max(c.y, c.z),
        c.y - max(c.x, c.z),
        c.z - max(c.x, c.y));
}

float3 NaturalMaterialMasks(float3 coverage)
{
    float3 d = CoverageMargins(coverage);
    float3 w = max(fwidth(d) * kMaterialMaskFwidthScale, kMaterialMaskMinWidth);
    float3 m = smoothstep(-w, w, d);
    float total = m.x + m.y + m.z;
    return total > 1e-4 ? m / total : float3(0.0, 0.0, 0.0);
}

// Gold veins are one cell wide and wander diagonally, so they are drawn from
// the exact gold cells rather than from a blurred field: a sum of Gaussian
// blobs at the gold cell centres of the 3x3 neighbourhood. Its kGoldVeinIso
// isoline is a band about one cell wide that stays connected across diagonal steps.
float GoldVeinField(float2 worldCell, int2 cell, int2 cellMax)
{
    float field = 0.0;
    for (int oy = -1; oy <= 1; oy++)
    {
        for (int ox = -1; ox <= 1; ox++)
        {
            int2 c = clamp(cell + int2(ox, oy), int2(0, 0), cellMax);
            float isGold = DecodeMaterialCode(auxTex.Load(int3(c, 0)).b) == kMaterialCodeGold ? 1.0 : 0.0;
            float2 d = worldCell - (float2(c) + 0.5);
            field += isGold * exp(-dot(d, d) * kGoldVeinFalloff);
        }
    }
    return field;
}

// Contact shadow on the dirt side and a bevel rim on the stone side of a
// stone/dirt boundary, so rock reads as sitting above the soil. The stone
// margin's gradient (per cell, sampled by the caller outside flow control) gives
// the boundary normal and a distance estimate (cells, positive inside stone).
void ApplyStoneDirtDepth(inout float3 color, float2 marginGrad, float stoneMargin, float natural)
{
    float2 lightDir2 = normalize(LightDir.xy);
    float gradLen = length(marginGrad);
    if (gradLen < 0.02)
        return;

    float2 outward = -marginGrad / gradLen;              // stone -> dirt
    float distCells = stoneMargin / gradLen;             // + stone side, - dirt side
    float facing = dot(outward, lightDir2);              // + faces the light

    // Dirt side: contact shadow, deeper where the dirt faces away from the light.
    float shadowT = saturate(1.0 + distCells / kStoneContactShadowCells);
    shadowT *= shadowT * (1.0 - smoothstep(0.0, 0.25, distCells));
    float shadow = (kStoneContactShadowBase + kStoneContactShadowLit * saturate(-facing)) * shadowT;

    // Stone side: narrow lit / shaded bevel.
    float rim = (1.0 - smoothstep(0.0, kStoneBevelCells, distCells)) * smoothstep(-0.25, 0.0, distCells);
    float3 rimColor = color * (1.0 + kStoneBevelLit * saturate(facing)) + float3(0.05, 0.05, 0.05) * saturate(facing);
    rimColor *= 1.0 - kStoneBevelShade * saturate(-facing);

    color *= 1.0 - shadow * natural;
    color = lerp(color, rimColor, rim * natural);
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
        PostVignetteEnabled <= 0.5 &&
        PostTankGlowEnabled <= 0.5;

    // ---- Coordinate setup & 3x3 aux neighborhood -------------------------
    float2 screenPx = uv * ViewSize;
    float2 worldCell = (CameraPixels + screenPx) / PixelScale;
    float2 auxUv = worldCell / WorldSize;
    float2 mTexel = float2(1.0 / WorldSize.x, 1.0 / WorldSize.y);

    // The cave/solid edge, shared with TerrainPs (uses derivatives: keep outside flow control).
    TerrainEdge edge = EvalTerrainEdge(worldCell);

    float4 a0  = auxTex.Sample(s1, auxUv);
    float4 ax1 = auxTex.Sample(s1, auxUv + float2( mTexel.x,  0.0));
    float4 ax2 = auxTex.Sample(s1, auxUv + float2(-mTexel.x,  0.0));
    float4 ay1 = auxTex.Sample(s1, auxUv + float2( 0.0,  mTexel.y));
    float4 ay2 = auxTex.Sample(s1, auxUv + float2( 0.0, -mTexel.y));
    float4 ad1 = auxTex.Sample(s1, auxUv + float2( mTexel.x,  mTexel.y));
    float4 ad2 = auxTex.Sample(s1, auxUv + float2( mTexel.x, -mTexel.y));
    float4 ad3 = auxTex.Sample(s1, auxUv + float2(-mTexel.x,  mTexel.y));
    float4 ad4 = auxTex.Sample(s1, auxUv + float2(-mTexel.x, -mTexel.y));

    // ---- Material sampling (needed for base exclusion and texturing) -------
    int2 auxMax = int2(max(1.0, WorldSize.x), max(1.0, WorldSize.y)) - int2(1, 1);
    int2 auxCell = clamp(int2(floor(worldCell)), int2(0, 0), auxMax);
    float4 aNearest = auxTex.Load(int3(auxCell, 0));
    int materialCodeNearest = DecodeMaterialCode(aNearest.b);
    float baseInfluence = IsBaseCode(materialCodeNearest) ? 1.0 : 0.0;
    // Scorch remnants along an edge read better without a crisp rim accent.
    float scorchNeighbor = max(
        max(max(a0.a, ax1.a), max(ax2.a, ay1.a)),
        max(max(ay2.a, ad1.a), max(ad2.a, max(ad3.a, ad4.a))));
    float scorchEdgeSoften = smoothstep(0.03, 0.14, scorchNeighbor) * (1.0 - baseInfluence);

    if (textureEnabled)
    {
        // ---- Material classification --------------------------------------
        // Natural materials come from the blurred coverage field; the exact
        // (nearest) code only decides where the base is. Base cells are drawn
        // with their own mask.
        float natural = terrainFactor * (1.0 - baseInfluence);
        float3 coverage = coverageTex.Sample(s2, auxUv).rgb;
        float3 margins = CoverageMargins(coverage);
        float3 naturalMasks = NaturalMaterialMasks(coverage);
        float stoneMask = naturalMasks.x;
        float concreteMask = naturalMasks.y;
        float dirtMask = naturalMasks.z;
        float naturalTotal = stoneMask + concreteMask + dirtMask;
        float goldField = GoldVeinField(worldCell, auxCell, auxMax);
        float baseMask = terrainFactor * baseInfluence;
        // Stone margin gradient (per cell) for the stone/dirt depth cue.
        float2 stoneMarginGrad = 0.5 * float2(
            CoverageMargins(coverageTex.Sample(s2, auxUv + float2(mTexel.x, 0.0)).rgb).x -
            CoverageMargins(coverageTex.Sample(s2, auxUv - float2(mTexel.x, 0.0)).rgb).x,
            CoverageMargins(coverageTex.Sample(s2, auxUv + float2(0.0, mTexel.y)).rgb).x -
            CoverageMargins(coverageTex.Sample(s2, auxUv - float2(0.0, mTexel.y)).rgb).x);

        // ---- Procedural material, composited over the cave by the edge ----
        // Each material is evaluated in full and blended by mask weight, then
        // the result replaces the scene colour on the solid side of the edge.
        float3 lightDir = normalize(float3(LightDir.xy, max(0.35, LightDir.z)));
        if (naturalTotal > 0.001 && natural > 0.0)
        {
            float3 blended = float3(0.0, 0.0, 0.0);
            if (stoneMask > 0.001)
            {
                float3 c = color;
                ApplyStoneMaterial(c, worldCell, 1.0, lightDir);
                blended += c * stoneMask;
            }
            if (concreteMask > 0.001)
            {
                float3 c = color;
                ApplyConcreteMaterial(c, worldCell, 1.0, lightDir);
                blended += c * concreteMask;
            }
            if (dirtMask > 0.001)
            {
                float3 c = color;
                ApplyDirtMaterial(c, worldCell, 1.0, lightDir);
                blended += c * dirtMask;
            }
            float3 materialColor = blended / naturalTotal;

            // Depth at the stone/dirt boundary.
            ApplyStoneDirtDepth(materialColor, stoneMarginGrad, margins.x, 1.0);

            // Rim light on the solid side of the cave edge, brighter where it faces the light.
            float facing = dot(-edge.normal, normalize(LightDir.xy));
            float rim = (1.0 - smoothstep(0.0, 1.2, edge.distCells)) * (1.0 - 0.90 * scorchEdgeSoften);
            float solidLift = terrainAuxOnlyMode ? TerrainMaskSolidLift * 0.35 : TerrainMaskSolidLift;
            float rimLift = terrainAuxOnlyMode ? 0.0 : TerrainMaskRimLift;
            materialColor += rim * (solidLift + rimLift * saturate(0.5 + 0.5 * facing));

            // Contact shadow on the cave floor just outside the edge.
            // Faded out on hot ground, where a dark rim would outline every island against the heat glow.
            float hotGround = saturate(a0.r * 6.0);
            float caveShadow = 1.0 - smoothstep(-1.0, 0.0, edge.distCells) * TerrainMaskCaveDarken * 4.0 * (1.0 - hotGround);
            float3 caveColor = kTerrainCaveColor * saturate(caveShadow);

            color = lerp(color, lerp(caveColor, materialColor, edge.coverage), natural * saturate(naturalTotal));
        }

        // Gold is a pickup the tank drives through, so it stays visible whatever
        // the cave/solid field says around it.
        ApplyGoldVein(color, worldCell, goldField, terrainFactor * (1.0 - baseInfluence), lightDir);

        ApplyBaseMaterial(color, worldCell, auxCell, auxMax, materialCodeNearest, baseMask);
    }

    // ---- Heat & scorch glow -----------------------------------------------
    if (heatEnabled)
    {
        ApplyTerrainHeatAndScorch(color, worldCell, auxUv, mTexel, terrainFactor, baseInfluence, edge.coverage,
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

        float h0 = auxTex.Sample(s1, tileCenterUv).r;
        float h1 = auxTex.Sample(s1, tileCenterUv + float2(tileSampleOffset.x, 0.0)).r;
        float h2 = auxTex.Sample(s1, tileCenterUv - float2(tileSampleOffset.x, 0.0)).r;
        float h3 = auxTex.Sample(s1, tileCenterUv + float2(0.0, tileSampleOffset.y)).r;
        float h4 = auxTex.Sample(s1, tileCenterUv - float2(0.0, tileSampleOffset.y)).r;
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
