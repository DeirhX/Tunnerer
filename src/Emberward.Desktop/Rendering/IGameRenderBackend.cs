namespace Emberward.Desktop.Rendering;

using System;
using Emberward.Core.Types;
using Emberward.Desktop.Config;

/// <param name="CameraPixels">The camera's top-left corner as drawn now, in whole pixels.</param>
/// <param name="CameraMotion">How far the camera moves in one simulation step (pixels): the shader draws it at
/// <c>CameraPixels - CameraMotion * StepAlpha + CameraMotion * alpha</c>, so a frame can be drawn again at a later alpha.</param>
/// <param name="StepAlpha">The progress through the current step (0..1) that <see cref="CameraPixels"/> is for.</param>
public readonly record struct RenderView(
    Size ViewSize,
    Size WorldSize,
    Position CameraPixels,
    int PixelScale,
    PositionF CameraMotion = default,
    float StepAlpha = 0f)
{
    /// <summary>The camera before its motion through the step: what the shader adds <c>CameraMotion * alpha</c> to.</summary>
    public PositionF CameraStartPixels => new(
        CameraPixels.X - CameraMotion.X * StepAlpha, CameraPixels.Y - CameraMotion.Y * StepAlpha);

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

/// <summary>
/// A camera that keeps a followed point (a tank's drawn start and motion, in cells) centred, the same way on the
/// desktop and in the browser: its position is <c>start + motion * alpha</c>, rounded to whole pixels so cells stay
/// exact blocks.
/// </summary>
public static class CameraFollow
{
    /// <summary>
    /// The camera for a view of <paramref name="viewSize"/> following a point that starts at <paramref name="start"/>
    /// and moves <paramref name="motion"/> (cells) through the step, at <paramref name="alpha"/> through it.
    /// </summary>
    public static (Position Pixels, PositionF Motion) At(
        PositionF start, PositionF motion, float alpha, int scale, Size viewSize, Size worldSize)
    {
        int minX = -viewSize.X / 2, minY = -viewSize.Y / 2;
        int maxX = worldSize.X * scale - viewSize.X / 2, maxY = worldSize.Y * scale - viewSize.Y / 2;
        float x0 = Math.Clamp(start.X * scale - viewSize.X / 2, minX, maxX);
        float y0 = Math.Clamp(start.Y * scale - viewSize.Y / 2, minY, maxY);
        float x1 = Math.Clamp((start.X + motion.X) * scale - viewSize.X / 2, minX, maxX);
        float y1 = Math.Clamp((start.Y + motion.Y) * scale - viewSize.Y / 2, minY, maxY);
        var pixels = new Position((int)MathF.Round(x0 + (x1 - x0) * alpha), (int)MathF.Round(y0 + (y1 - y0) * alpha));
        return (pixels, new PositionF(x1 - x0, y1 - y0));
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

    void ClearFrame(Size viewportSize, Emberward.Core.Types.Color clearColor);

    void NewFrame(int windowW, int windowH, float deltaTime);

    /// <summary>State of the HUD temperature orb for the frame being drawn (see <see cref="HeatOrbEffect"/>).</summary>
    void SetHeatOrb(in HeatOrbState state);

    void Render();

    void RequestScreenshot(string? label = null);
}
