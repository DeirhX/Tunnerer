// ============================================================================
// Materials/ConcreteMaterial.hlsli — Procedural concrete / manufactured overlay
// ============================================================================
//
// Smooth, uniform, man-made surface. Distinct from stone's organic granularity
// through lower noise frequency, subtle panel-line grid, and a cooler blue-gray
// palette vs stone's warm gray.
//
// Layers:
//   1. Smooth body — low-frequency noise for gentle surface variation
//   2. Panel seams — faint grid lines suggesting poured sections
//   3. Micro-pitting — very fine speckle to avoid looking perfectly flat
// ============================================================================

void ApplyConcreteMaterial(inout float3 color, float2 worldCell, float concreteMask, float3 lightDir)
{
    if (concreteMask <= 0.0)
        return;

    // ---- 1. Smooth body surface -------------------------------------------
    float2 cP = worldCell * 0.012;
    float c0 = fbmNoise(cP + float2(53.0, 317.0), 3);
    float cnx = fbmNoise(cP + float2(55.0, 317.0), 3);
    float cny = fbmNoise(cP + float2(53.0, 319.0), 3);
    float2 cGrad = float2(cnx - c0, cny - c0);
    float3 cNormal = normalize(float3(-cGrad * 3.0, 1.0));
    float cDiffuse = dot(cNormal, lightDir) * 0.5 + 0.5;

    // ---- 2. Panel seam grid -----------------------------------------------
    float2 panelUV = worldCell * 0.04;
    float2 panelFrac = frac(panelUV);
    float seamX = 1.0 - smoothstep(0.0, 0.06, min(panelFrac.x, 1.0 - panelFrac.x));
    float seamY = 1.0 - smoothstep(0.0, 0.06, min(panelFrac.y, 1.0 - panelFrac.y));
    float seam = max(seamX, seamY) * 0.12;

    // ---- 3. Micro-pitting (high-frequency speckle) ------------------------
    float pit = fbmNoise(worldCell * 0.22 + float2(191.0, 73.0), 2);
    float speckle = smoothstep(0.35, 0.65, pit) * 0.08;

    // ---- Composite --------------------------------------------------------
    float3 concreteTint = lerp(
        float3(0.52, 0.54, 0.58),
        float3(0.68, 0.70, 0.75),
        cDiffuse);

    float surface = 0.92 + 0.12 * cDiffuse - seam - speckle;
    color = lerp(color, concreteTint * surface, concreteMask * 0.80);
}
