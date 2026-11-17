namespace Tunnerer.Desktop.Rendering;

using System.Runtime.InteropServices;
using Tunnerer.Desktop.Config;

/// <summary>
/// The CPU side of <c>cbuffer PostParams</c> (Rendering/Shaders/PostPs/Common/PostParams.hlsli), shared by the
/// terrain and post passes of every backend.
/// </summary>
/// <remarks>
/// Each line below is one 16-byte constant-buffer row. HLSL never lets a <c>float4</c> straddle two rows, so the
/// struct pads the same way; <c>PostParamsLayoutGuardTests</c> compares every field's offset with the packing it
/// computes from the .hlsli file.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct PostParamsBlock
{
    public const float LayoutVersion = 5.0f;
    public const int MaxTankGlows = 8;

    public float TexelSizeX, TexelSizeY, PixelScale, Time;
    public float WorldSizeX, WorldSizeY, CameraPixelsX, CameraPixelsY;
    public float ViewSizeX, ViewSizeY, UseTerrainAux, PostParamsLayoutVersion;
    public float HeatDebugOverlay, PostVignetteEnabled, PostFogEnabled, PostTerrainAuxEnabled;
    public float PostTankGlowEnabled, PostTerrainHeatEnabled, PostThermalRegionsEnabled, ThermalTileSizeCells;
    public float ThermalTileHeatThreshold01, _padReserved1, VignetteStrength, VignetteInnerRadius;
    public float VignetteOuterRadius, _padReserved2, TerrainMaskCaveDarken, TerrainMaskSolidLift;
    public float _padReserved3, TerrainMaskRimLift, _padReserved4, Quality;
    public float MaterialScorchedR, MaterialScorchedG, MaterialScorchedB, MaterialScorchedStrength;
    public float MaterialPulseFreq, MaterialPulseMin, MaterialPulseRange, MaterialPulsePad;
    public float NativeSmoothingEnabled, _padReserved5, NativeSampleFactor, NativeCurvingEnabled;
    public float LightDirX, LightDirY, LightDirZ, LightNormalStrength;
    public float HalfVecX, HalfVecY, HalfVecZ, LightMicroNormalStrength;
    public float LightAmbient, LightDiffuseWeight, LightShininess, LightSpecularIntensity;
    public float TankHeatGlowR, TankHeatGlowG, TankHeatGlowB, TankHeatGlowA;
    public float TerrainHeatGlowR, TerrainHeatGlowG, TerrainHeatGlowB, TerrainHeatThreshold;
    public float _padScorch0, ScorchTintDarken, ScorchTintStrength, _padScorch1;
    public float TankGlowCount, _padTankGlow0, _padTankGlow1, _padTankGlow2;
    public fixed float TankGlow[MaxTankGlows * 4];

    /// <param name="time">Seconds since start, for animated effects.</param>
    public static PostParamsBlock Build(in GamePixelsUpload upload, float time)
    {
        PostParamsBlock cb = default;
        cb.TexelSizeX = 1f / Math.Max(1, upload.View.ViewSize.X);
        cb.TexelSizeY = 1f / Math.Max(1, upload.View.ViewSize.Y);
        cb.PixelScale = upload.View.PixelScale;
        cb.Time = time;
        cb.WorldSizeX = upload.View.WorldSize.X;
        cb.WorldSizeY = upload.View.WorldSize.Y;
        cb.CameraPixelsX = upload.View.CameraPixels.X;
        cb.CameraPixelsY = upload.View.CameraPixels.Y;
        cb.ViewSizeX = upload.View.ViewSize.X;
        cb.ViewSizeY = upload.View.ViewSize.Y;
        cb.UseTerrainAux = BoolToFloat(upload.TerrainAux.Data != null);
        cb.PostParamsLayoutVersion = LayoutVersion;
        PostProcessPassFlags passes = upload.PostProcess.PassFlags;
        cb.HeatDebugOverlay = BoolToFloat(upload.PostProcess.HeatDebugOverlayEnabled);
        cb.PostVignetteEnabled = BoolToFloat(HasPass(passes, PostProcessPassFlags.Vignette));
        cb.PostFogEnabled = BoolToFloat(upload.Fog.Data != null);
        cb.PostTerrainAuxEnabled = BoolToFloat(HasPass(passes, PostProcessPassFlags.TerrainAux));
        cb.PostTankGlowEnabled = BoolToFloat(HasPass(passes, PostProcessPassFlags.TankGlow));
        cb.PostTerrainHeatEnabled = BoolToFloat(HasPass(passes, PostProcessPassFlags.TerrainHeat));
        cb.PostThermalRegionsEnabled = BoolToFloat(HasPass(passes, PostProcessPassFlags.ThermalRegions));
        cb.ThermalTileSizeCells = upload.PostProcess.ThermalRegions.TileSizeCells;
        cb.ThermalTileHeatThreshold01 = upload.PostProcess.ThermalRegions.ActiveThresholdHeat01;
        cb.VignetteStrength = DesktopScreenTweaks.PostVignetteStrength;
        cb.VignetteInnerRadius = DesktopScreenTweaks.PostVignetteInnerRadius;
        cb.VignetteOuterRadius = DesktopScreenTweaks.PostVignetteOuterRadius;
        cb.TankHeatGlowR = DesktopScreenTweaks.PostTankHeatGlowR;
        cb.TankHeatGlowG = DesktopScreenTweaks.PostTankHeatGlowG;
        cb.TankHeatGlowB = DesktopScreenTweaks.PostTankHeatGlowB;
        cb.TankHeatGlowA = DesktopScreenTweaks.PostTankHeatDistortionEnabled ? 1f : 0f;
        cb.TerrainHeatGlowR = DesktopScreenTweaks.PostTerrainHeatGlowR;
        cb.TerrainHeatGlowG = DesktopScreenTweaks.PostTerrainHeatGlowG;
        cb.TerrainHeatGlowB = DesktopScreenTweaks.PostTerrainHeatGlowB;
        cb.TerrainHeatThreshold = DesktopScreenTweaks.PostTerrainHeatThreshold;
        cb.TerrainMaskCaveDarken = DesktopScreenTweaks.PostTerrainMaskCaveDarken;
        cb.TerrainMaskSolidLift = DesktopScreenTweaks.PostTerrainMaskSolidLift;
        cb.TerrainMaskRimLift = DesktopScreenTweaks.PostTerrainMaskRimLift;
        cb.Quality = 2f; // full detail; the vignette pass runs from Quality 2
        cb.MaterialScorchedR = DesktopScreenTweaks.PostMaterialEmissiveScorchedR;
        cb.MaterialScorchedG = DesktopScreenTweaks.PostMaterialEmissiveScorchedG;
        cb.MaterialScorchedB = DesktopScreenTweaks.PostMaterialEmissiveScorchedB;
        cb.MaterialScorchedStrength = DesktopScreenTweaks.PostMaterialEmissiveScorchedStrength;
        cb.MaterialPulseFreq = DesktopScreenTweaks.PostMaterialEmissivePulseFreq;
        cb.MaterialPulseMin = DesktopScreenTweaks.PostMaterialEmissivePulseMin;
        cb.MaterialPulseRange = DesktopScreenTweaks.PostMaterialEmissivePulseRange;
        cb.MaterialPulsePad = 0f;
        cb.NativeSmoothingEnabled = BoolToFloat(HasPass(passes, PostProcessPassFlags.NativeTerrainSmoothing));
        cb.NativeSampleFactor = upload.NativeContinuous.SampleCount <= 1
            ? 0f
            : upload.NativeContinuous.SampleCount <= 2 ? 0.5f : 1f;
        cb.NativeCurvingEnabled = BoolToFloat(HasPass(passes, PostProcessPassFlags.NativeEdgeCurving));

        float lx = DesktopScreenTweaks.LightDirX, ly = DesktopScreenTweaks.LightDirY, lz = DesktopScreenTweaks.LightDirZ;
        Normalize3(ref lx, ref ly, ref lz);
        cb.LightDirX = lx;
        cb.LightDirY = ly;
        cb.LightDirZ = lz;
        cb.LightNormalStrength = DesktopScreenTweaks.LightNormalStrength;
        float hx = lx, hy = ly, hz = lz + 1f;
        Normalize3(ref hx, ref hy, ref hz);
        cb.HalfVecX = hx;
        cb.HalfVecY = hy;
        cb.HalfVecZ = hz;
        cb.LightMicroNormalStrength = DesktopScreenTweaks.LightMicroNormalStrength;
        cb.LightAmbient = DesktopScreenTweaks.LightAmbient;
        cb.LightDiffuseWeight = DesktopScreenTweaks.LightDiffuseWeight;
        cb.LightShininess = DesktopScreenTweaks.LightShininess;
        cb.LightSpecularIntensity = DesktopScreenTweaks.LightSpecularIntensity;
        cb.ScorchTintDarken = DesktopScreenTweaks.ScorchTintDarken;
        cb.ScorchTintStrength = DesktopScreenTweaks.ScorchTintStrength;

        int glowCount = Math.Clamp(upload.TankGlow.Count, 0, MaxTankGlows);
        cb.TankGlowCount = glowCount;
        if (upload.TankGlow.Data != null)
        {
            for (int i = 0; i < glowCount * 4; i++)
                cb.TankGlow[i] = upload.TankGlow.Data[i];
        }
        return cb;
    }

    private static float BoolToFloat(bool value) => value ? 1f : 0f;

    private static bool HasPass(PostProcessPassFlags flags, PostProcessPassFlags pass) => (flags & pass) != 0;

    private static void Normalize3(ref float x, ref float y, ref float z)
    {
        float len = MathF.Sqrt(x * x + y * y + z * z);
        if (len > 0.001f)
        {
            x /= len;
            y /= len;
            z /= len;
        }
    }
}
