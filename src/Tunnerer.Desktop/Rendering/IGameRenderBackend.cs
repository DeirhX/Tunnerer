namespace Tunnerer.Desktop.Rendering;

using System;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Config;

public readonly record struct RenderView(
    Size ViewSize,
    Size WorldSize,
    Position CameraPixels,
    int PixelScale)
{
    /// <summary>The terrain cells the view shows any part of, clipped to the world.</summary>
    public Rect VisibleCells()
    {
        int scale = Math.Max(1, PixelScale);
        int minX = Math.Max(0, CameraPixels.X / scale);
        int minY = Math.Max(0, CameraPixels.Y / scale);
        int maxX = Math.Min(WorldSize.X, (CameraPixels.X + ViewSize.X + scale - 1) / scale);
        int maxY = Math.Min(WorldSize.Y, (CameraPixels.Y + ViewSize.Y + scale - 1) / scale);
        return new Rect(minX, minY, Math.Max(0, maxX - minX), Math.Max(0, maxY - minY));
    }
}

public readonly record struct GamePixelsUpload(
    RenderView View,
    PostProcessUploadOptions PostProcess,
    TankGlowUpload TankGlow,
    TerrainAuxUpload TerrainAux,
    NativeContinuousUpload NativeContinuous,
    EntityLayerUpload Entities,
    FogUpload Fog = default,
    ScreenEffectLevels ScreenEffects = default);

/// <param name="TimeSeconds">The shaders' animation clock; also the clock regrow stamps are written in.</param>
public readonly record struct PostProcessUploadOptions(
    bool HeatDebugOverlayEnabled,
    PostProcessPassFlags PassFlags,
    ThermalRegionOverlayUpload ThermalRegions,
    float TimeSeconds);

public readonly record struct ThermalRegionOverlayUpload(
    int TileSizeCells,
    float ActiveThresholdHeat01);

public readonly record struct TankGlowUpload(
    float[]? Data,
    int Count);

public readonly record struct TerrainAuxUpload(
    byte[]? Data,
    Rect? DirtyRect,
    byte[]? MaterialCoverage,
    Rect? MaterialCoverageDirtyRect);

/// <summary>
/// The fog of war texture, one RGBA texel per terrain cell (see <see cref="FogLayer"/>). Null <paramref name="Data"/>
/// means no fog: everything is drawn in plain sight.
/// </summary>
public readonly record struct FogUpload(
    byte[]? Data,
    Rect? DirtyRect);

/// <param name="SourceChanged">False when <paramref name="SourcePixels"/> is the same image as the last upload.</param>
public readonly record struct NativeContinuousUpload(
    uint[]? SourcePixels,
    bool SourceChanged,
    int SampleCount);

/// <summary>Quads for the entity layer (tanks, projectiles), drawn over the finished terrain frame.</summary>
public readonly record struct EntityLayerUpload(
    EntityVertex[]? Vertices,
    int VertexCount);

/// <summary>
/// The HUD temperature orb, drawn by a shader (HeatOrbPs) instead of a texture: the HUD adds an ImGui image with
/// <see cref="TextureId"/> and hands the backend the orb's state each frame.
/// </summary>
public static class HeatOrbEffect
{
    /// <summary>Reserved ImGui texture id the renderer swaps for the orb shader. Never a real texture.</summary>
    public static readonly nint TextureId = 0x4E42524F; // "ORBN"
}

/// <param name="LiquidColor">Colour of the liquid for the temperature (rgb 0..1).</param>
/// <param name="Frozen">0 at ambient, 1 at the frostbite temperature: the liquid turns to ice.</param>
/// <param name="Boil">0 calm, 1 at the safe maximum: bubbles, churn and a glowing core.</param>
/// <param name="Burning">0 normally; pulses up to 1 while overheating burns the shield.</param>
/// <param name="Time">Seconds, for the animation.</param>
public readonly record struct HeatOrbState(
    System.Numerics.Vector3 LiquidColor,
    float Frozen,
    float Boil,
    float Burning,
    float Time);

public interface IGameRenderBackend : IDisposable
{
    nint GameTextureId { get; }
    Size GameTextureSize { get; }
    bool SupportsUi { get; }

    void ProcessEvent(in RenderInputEvent ev);

    void UploadGamePixels(in GamePixelsUpload upload);

    void ClearFrame(Size viewportSize, Tunnerer.Core.Types.Color clearColor);

    void NewFrame(int windowW, int windowH, float deltaTime);

    /// <summary>State of the HUD temperature orb for the frame being drawn (see <see cref="HeatOrbEffect"/>).</summary>
    void SetHeatOrb(in HeatOrbState state);

    void Render();

    void RequestScreenshot(string? label = null);
}
