namespace Tunnerer.Desktop.Rendering;

using System;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Config;

public readonly record struct RenderView(
    Size ViewSize,
    Size WorldSize,
    Position CameraPixels,
    int PixelScale);

public readonly record struct GamePixelsUpload(
    uint[] Pixels,
    RenderView View,
    PostProcessUploadOptions PostProcess,
    TankGlowUpload TankGlow,
    TerrainAuxUpload TerrainAux,
    NativeContinuousUpload NativeContinuous,
    EntityLayerUpload Entities);

public readonly record struct PostProcessUploadOptions(
    HiResRenderQuality Quality,
    bool HeatDebugOverlayEnabled,
    PostProcessPassFlags PassFlags,
    ThermalRegionOverlayUpload ThermalRegions);

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

/// <param name="SourceChanged">False when <paramref name="SourcePixels"/> is the same image as the last upload.</param>
public readonly record struct NativeContinuousUpload(
    bool Enabled,
    uint[]? SourcePixels,
    bool SourceChanged,
    int SampleCount);

/// <summary>Quads for the entity layer (tanks, projectiles), drawn over the finished terrain frame.</summary>
public readonly record struct EntityLayerUpload(
    EntityVertex[]? Vertices,
    int VertexCount);

public interface IGameRenderBackend : IDisposable
{
    nint GameTextureId { get; }
    Size GameTextureSize { get; }
    bool SupportsUi { get; }

    void ProcessEvent(in RenderInputEvent ev);

    void UploadGamePixels(in GamePixelsUpload upload);

    void ClearFrame(Size viewportSize, Tunnerer.Core.Types.Color clearColor);

    void NewFrame(int windowW, int windowH, float deltaTime);

    void Render();

    void RequestScreenshot(string? label = null);
}
