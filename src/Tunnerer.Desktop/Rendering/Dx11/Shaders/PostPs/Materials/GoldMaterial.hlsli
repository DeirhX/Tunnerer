// ============================================================================
// Materials/GoldMaterial.hlsli — Procedural gold crystal overlay
// ============================================================================
//
// Applies a warm metallic body tint and pulsing emissive veins to pixels
// classified as gold terrain. Simpler than stone/dirt since the gold
// material is meant to look uniform and glowing.
//
// Layers:
//   1. Crystalline body — diffuse-lit golden tint from normal-mapped noise
//   2. Emissive veins   — ridged noise pattern with time-animated pulse
// ============================================================================

void ApplyGoldMaterial(inout float3 color, float2 worldCell, float goldMask, float3 lightDir)
{
    if (goldMask <= 0.0)
        return;

    // ---- 1. Crystalline body diffuse --------------------------------------
    float2 eP = worldCell * 0.060;
    float e0 = fbmNoise(eP + float2(7.0, 241.0), 4);
    float enx = fbmNoise(eP + float2(9.0, 241.0), 4);
    float eny = fbmNoise(eP + float2(7.0, 243.0), 4);
    float2 eGrad = float2(enx - e0, eny - e0);
    float3 eNormal = normalize(float3(-eGrad * 6.0, 1.0));
    float eDiffuse = dot(eNormal, lightDir) * 0.5 + 0.5;

    // ---- 2. Emissive veins with animated pulse ----------------------------
    float veins = smoothstep(0.76, 0.95, 1.0 - abs(e0 * 2.0 - 1.0));
    float pulseE = MaterialEmissiveGold.y + MaterialEmissiveGold.z * (0.5 + 0.5 * sin(Time * MaterialEmissiveGold.x + e0 * 9.0));
    float3 goldTint = lerp(float3(0.85, 0.68, 0.30), float3(1.00, 0.88, 0.45), eDiffuse);

    color = lerp(color, color * goldTint * (0.94 + 0.14 * eDiffuse), goldMask * 0.85);
    color += MaterialEmissiveGold.rgb * (veins * MaterialEmissiveGold.a * pulseE * goldMask);
}
