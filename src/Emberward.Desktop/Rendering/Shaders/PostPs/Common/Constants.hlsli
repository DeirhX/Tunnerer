// ============================================================================
// Common/Constants.hlsli — Shared compile-time constants
// ============================================================================
//
// Numeric constants used across multiple post-processing passes. Grouped by
// the subsystem they belong to. Keeping them here avoids magic numbers in
// pass code and makes tuning easier.
// ============================================================================

// ---- Perceptual luminance weights (Rec. 601) ------------------------------
static const float3 kLumaWeights = float3(0.299, 0.587, 0.114);
static const float kTerrainAlphaThreshold = 0.999;

// ---- Epsilon helpers ------------------------------------------------------
static const float2 kSignedEpsilon2 = float2(1e-4, -1e-4);
static const float kRadiusEpsilonPx = 1e-3;

float2 TexelOffsetX()
{
    return float2(TexelSize.x, 0.0);
}

float2 TexelOffsetY()
{
    return float2(0.0, TexelSize.y);
}

// ---- Terrain material code (auxTex.b channel) -----------------------------
// CPU writes the code as a byte in a normalized channel. Codes are categorical:
// only ever read them with Load (nearest cell) and compare exactly; never
// interpolate. Natural materials (dirt/stone/concrete) are not told apart
// here but by coverageTex; their code (100) just means "natural solid".
// Pyrite ore (plain and core) has its own codes so it can be drawn from the
// exact cells.
// Must match TerrainAuxBuilder.
static const float kMaterialCodeScale = 255.0;
static const int kMaterialCodeOre = 120;
static const int kMaterialCodeOreCore = 121;
static const int kMaterialCodeBaseFloor = 20;
static const int kMaterialCodeBaseCore = 25;
// The core is a disc of cells 5 across (Tweaks.Keep.CoreSize). A core cell has at most kBaseCoreReach others of the
// core beside it in a row, and kBaseCoreRadius is the largest circle that stays inside the core's cells.
static const int kBaseCoreReach = 4;
static const float kBaseCoreRadius = 2.1;
static const int kMaterialCodeBaseDoor = 30;      // a gate, shut to the enemy by the gate shield
static const int kMaterialCodeBaseDoorOpen = 31;  // a gate whose shield is spent
static const int kMaterialCodeBaseWallMin = 40;   // wall code = 40 + owner colour index
static const int kMaterialCodeBaseWallMax = 47;

int DecodeMaterialCode(float encoded)
{
    return (int)round(encoded * kMaterialCodeScale);
}

bool IsBaseWallCode(int code)  { return code >= kMaterialCodeBaseWallMin && code <= kMaterialCodeBaseWallMax; }
bool IsBaseFloorCode(int code) { return code == kMaterialCodeBaseFloor; }
bool IsBaseCoreCode(int code)  { return code == kMaterialCodeBaseCore; }
bool IsBaseDoorCode(int code)  { return code == kMaterialCodeBaseDoor || code == kMaterialCodeBaseDoorOpen; }
bool IsBaseCode(int code)      { return IsBaseWallCode(code) || IsBaseFloorCode(code) || IsBaseDoorCode(code) || IsBaseCoreCode(code); }
// What stands up from the floor and shades it: walls and baffles. The core is round and shades its own cells.
bool IsBaseRaisedCode(int code) { return IsBaseWallCode(code); }

// ---- Natural material selection from coverageTex ---------------------------
static const float kMaterialMaskMinWidth = 0.01;      // minimum smoothstep half-width on coverage differences
static const float kMaterialMaskFwidthScale = 0.75;   // anti-aliasing width relative to screen-space change

// ---- Pyrite ore ------------------------------------------------------------
static const float kOreFalloff = 1.1;                 // blob width around each ore cell centre (deposits are solid
                                                      // blobs)
static const float kOreIso = 0.55;                    // field value at the edge of a deposit
static const float kOreGlowMin = 0.2;                 // share of the glow left in cold ore
static const float kOreGlowFullTemperature = 25.0;    // ore at or above this (degrees) glows fully
static const float kOreGlowReach = 0.05;              // field value where an ore cell's halo fades out (~1.5 cells away)

// ---- Stone / dirt boundary depth -------------------------------------------
static const float kStoneContactShadowCells = 1.10;   // dirt-side shadow reach (cells; 6 screen px per cell)
static const float kStoneContactShadowBase = 0.26;    // darkening where the dirt faces the light
static const float kStoneContactShadowLit = 0.12;     // extra darkening where it faces away
static const float kStoneBevelCells = 0.55;           // stone-side bevel width (cells)
static const float kStoneBevelLit = 0.30;             // brightening on the light-facing bevel
static const float kStoneBevelShade = 0.28;           // darkening on the shaded bevel

// ---- Heat encoding ---------------------------------------------------------
// auxTex.r = (temperature + 80) / 4 / 255 (TerrainGrid.HeatToByte). Subtract kHeatZero for the signed heat in the
// shader's units (1.0 = 1020 degrees); negative is below ambient.
static const float kHeatZero = 80.0 / 4.0 / 255.0;
static const float kHeatByteToDegrees = 4.0 * 255.0;  // (aux.r - kHeatZero) * this = degrees
// Frost reaches full strength at the starting temperature of frost rock.
static const float kFrostFullHeat = 60.0 / (4.0 * 255.0);

// ---- Heat debug overlay ---------------------------------------------------
static const float kHeatDebugTemperatureScale = 255.0 * 4.0;
