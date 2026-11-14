// ============================================================================
// Materials/BaseMaterial.hlsli — Procedural tank-base wall, floor and door
// ============================================================================
//
// A base is a built square, so it is drawn from the *nearest* cell's exact
// material code (auxTex.b) and the codes of the cells around it; nothing here
// is interpolated. The look comes from frac(worldCell) inside each cell:
//
//   Wall  — 1-cell beveled steel band: bright light-facing edge, dark far edge,
//           a stripe in the owner's colour down the middle.
//   Floor — recessed dark plating: panel grid every 4 cells, light noise, and
//           an inner drop shadow that fades over ~1.5 cells from any wall.
//   Door  — energy barrier: cyan beam with a slow pulse and flowing bands,
//           framed by amber hazard stripes.
//
// Codes are decoded in Constants.hlsli (DecodeMaterialCode / IsBase*Code).
// ============================================================================

// Owner colours, index = wall code - kMaterialCodeBaseWallMin.
// Mirrors TankSprites.TankColors[i][0] in Tunnerer.Core.
static const float3 kBaseOwnerColors[8] =
{
    float3(0x2c, 0x2c, 0xff) / 255.0,   // blue
    float3(0x00, 0xff, 0x00) / 255.0,   // green
    float3(0xff, 0x00, 0x00) / 255.0,   // red
    float3(0xff, 0x99, 0x99) / 255.0,   // pink
    float3(0xff, 0x00, 0xff) / 255.0,   // purple
    float3(0xee, 0xee, 0xee) / 255.0,   // white
    float3(0x00, 0xff, 0xff) / 255.0,   // aqua
    float3(0x66, 0x66, 0x66) / 255.0,   // gray
};

static const float kWallBevelWidth = 0.24;    // cells
static const float kWallStripeHalfWidth = 0.17; // cells
static const float kFloorPanelCells = 4.0;
static const float kFloorShadowCells = 1.5;

int LoadMaterialCode(int2 cell, int2 offset, int2 maxCell)
{
    int2 c = clamp(cell + offset, int2(0, 0), maxCell);
    return DecodeMaterialCode(auxTex.Load(int3(c, 0)).b);
}

float3 ShadeBaseWall(float2 f, float2 worldCell, int code, int cL, int cR, int cU, int cD)
{
    bool wL = IsBaseWallCode(cL);
    bool wR = IsBaseWallCode(cR);
    bool wU = IsBaseWallCode(cU);
    bool wD = IsBaseWallCode(cD);

    // Brushed steel with a little per-cell and per-pixel variation.
    float cellTone = hash21(floor(worldCell) + float2(17.0, 5.0));
    float brush = valueNoise(worldCell * float2(3.0, 3.0) + float2(11.0, 29.0));
    float3 steel = float3(0.43, 0.46, 0.52) * (0.92 + 0.08 * cellTone + 0.08 * brush);

    // Bevel: only on sides where the band ends (outer face, inner face, door gap).
    float bw = kWallBevelWidth;
    float edgeT = wU ? 0.0 : 1.0 - smoothstep(0.0, bw, f.y);
    float edgeL = wL ? 0.0 : 1.0 - smoothstep(0.0, bw, f.x);
    float edgeB = wD ? 0.0 : 1.0 - smoothstep(0.0, bw, 1.0 - f.y);
    float edgeR = wR ? 0.0 : 1.0 - smoothstep(0.0, bw, 1.0 - f.x);
    float lit = max(edgeT, edgeL);
    float shade = max(edgeB, edgeR);
    steel = lerp(steel, float3(0.86, 0.90, 0.96), lit * 0.90);
    steel = lerp(steel, float3(0.06, 0.07, 0.09), shade * 0.88);

    // Owner stripe along the band's axis (an end cap at the centre where it stops).
    float sw = kWallStripeHalfWidth;
    float dx = abs(f.x - 0.5);
    float dy = abs(f.y - 0.5);
    float hSeg = max((wL && f.x < 0.5) ? 1.0 : 0.0, (wR && f.x >= 0.5) ? 1.0 : 0.0);
    float vSeg = max((wU && f.y < 0.5) ? 1.0 : 0.0, (wD && f.y >= 0.5) ? 1.0 : 0.0);
    hSeg = max(hSeg, 1.0 - step(sw, dx));
    vSeg = max(vSeg, 1.0 - step(sw, dy));
    float stripeH = hSeg * (1.0 - smoothstep(sw - 0.05, sw, dy));
    float stripeV = vSeg * (1.0 - smoothstep(sw - 0.05, sw, dx));
    float stripe = max(stripeH, stripeV);
    float3 owner = kBaseOwnerColors[clamp(code - kMaterialCodeBaseWallMin, 0, 7)];
    steel = lerp(steel, owner * 0.95, stripe);
    return steel;
}

float3 ShadeBaseFloor(float2 f, float2 worldCell, int2 cell, int2 maxCell, int cL, int cR, int cU, int cD)
{
    float3 plate = float3(0.100, 0.118, 0.150);

    // Plating: panel seams every kFloorPanelCells cells (light line + dark line behind it).
    float2 pm = frac(worldCell / kFloorPanelCells) * kFloorPanelCells;
    float seamLight = max(1.0 - smoothstep(0.10, 0.28, pm.x), 1.0 - smoothstep(0.10, 0.28, pm.y));
    float seamDark = max(
        smoothstep(0.28, 0.36, pm.x) * (1.0 - smoothstep(0.36, 0.60, pm.x)),
        smoothstep(0.28, 0.36, pm.y) * (1.0 - smoothstep(0.36, 0.60, pm.y)));
    plate += 0.07 * seamLight;
    plate *= 1.0 - 0.30 * seamDark;
    float noise = hash21(floor(worldCell) + float2(3.0, 71.0)) * 0.020
                + (valueNoise(worldCell * 0.8 + float2(5.0, 13.0)) - 0.5) * 0.030;
    plate += noise;

    // Inner drop shadow from walls 1 and 2 cells away, strongest under the light-facing walls.
    int cL2 = LoadMaterialCode(cell, int2(-2, 0), maxCell);
    int cR2 = LoadMaterialCode(cell, int2(2, 0), maxCell);
    int cU2 = LoadMaterialCode(cell, int2(0, -2), maxCell);
    int cD2 = LoadMaterialCode(cell, int2(0, 2), maxCell);
    float dL = IsBaseWallCode(cL) ? f.x : (IsBaseWallCode(cL2) ? f.x + 1.0 : 9.0);
    float dR = IsBaseWallCode(cR) ? 1.0 - f.x : (IsBaseWallCode(cR2) ? 2.0 - f.x : 9.0);
    float dU = IsBaseWallCode(cU) ? f.y : (IsBaseWallCode(cU2) ? f.y + 1.0 : 9.0);
    float dD = IsBaseWallCode(cD) ? 1.0 - f.y : (IsBaseWallCode(cD2) ? 2.0 - f.y : 9.0);
    float aoLit = 1.0 - smoothstep(0.0, kFloorShadowCells, min(dL, dU));
    float aoFar = 1.0 - smoothstep(0.0, kFloorShadowCells, min(dR, dD));
    plate *= 1.0 - max(aoLit * 0.70, aoFar * 0.50);
    return plate;
}

float3 ShadeBaseDoor(float2 f, float2 worldCell, int cL, int cR, int cU, int cD)
{
    bool vertical = (IsBaseDoorCode(cU) || IsBaseDoorCode(cD)) && !(IsBaseDoorCode(cL) || IsBaseDoorCode(cR));
    float axisPos = vertical ? worldCell.y : worldCell.x;
    float across = vertical ? abs(f.x - 0.5) : abs(f.y - 0.5);
    float acrossPos = vertical ? f.x : f.y;

    float3 bg = float3(0.040, 0.075, 0.100);
    float pulse = 0.78 + 0.22 * sin(Time * 2.4 + axisPos * 0.35);
    float flow = 0.5 + 0.5 * sin(axisPos * 3.0 - Time * 4.0);
    float core = 1.0 - smoothstep(0.0, 0.16, across);
    float halo = 1.0 - smoothstep(0.0, 0.48, across);
    float3 beam = float3(0.15, 0.85, 1.00);
    float3 color = bg + beam * (halo * 0.30 * pulse + core * (0.55 + 0.35 * flow) * pulse);
    color += float3(0.75, 0.95, 1.0) * core * core * 0.25 * pulse;

    // Amber hazard rim along both long edges.
    float rim = smoothstep(0.34, 0.40, across);
    float bars = step(0.5, frac((axisPos + acrossPos) * 0.5));
    float3 hazard = lerp(float3(0.10, 0.07, 0.02), float3(0.95, 0.62, 0.08), bars);
    return lerp(color, hazard, rim);
}

// Draws the base cell under this pixel. `code` is the nearest cell's material code.
void ApplyBaseMaterial(inout float3 color, float2 worldCell, int2 cell, int2 maxCell, int code, float baseMask)
{
    if (baseMask <= 0.0)
        return;

    float2 f = frac(worldCell);
    int cL = LoadMaterialCode(cell, int2(-1, 0), maxCell);
    int cR = LoadMaterialCode(cell, int2(1, 0), maxCell);
    int cU = LoadMaterialCode(cell, int2(0, -1), maxCell);
    int cD = LoadMaterialCode(cell, int2(0, 1), maxCell);

    float3 baseColor;
    if (IsBaseWallCode(code))
        baseColor = ShadeBaseWall(f, worldCell, code, cL, cR, cU, cD);
    else if (IsBaseDoorCode(code))
        baseColor = ShadeBaseDoor(f, worldCell, cL, cR, cU, cD);
    else
        baseColor = ShadeBaseFloor(f, worldCell, cell, maxCell, cL, cR, cU, cD);

    color = lerp(color, baseColor, baseMask);
}
