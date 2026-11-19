namespace Emberward.Desktop.Rendering;

using Emberward.Core.Types;
using Emberward.Desktop.Config;
using Emberward.Desktop.Gui;

public sealed class RenderViewState
{
    private int _nativeOverBudgetFrames;
    private int _nativeUnderBudgetFrames;

    /// <summary>Size of the game view in device pixels.</summary>
    public Size HiResSize { get; private set; }
    /// <summary>Camera position of the view's top-left corner, in device pixels.</summary>
    public int CamPixelX { get; private set; }
    public int CamPixelY { get; private set; }
    public int NativeContinuousSampleCount { get; private set; } = DesktopScreenTweaks.NativeContinuousSampleHigh;

    /// <summary>Device pixels per point of the window: 2 on Retina, 1 on a standard display.</summary>
    public float PixelDensity { get; private set; } = 1f;

    /// <summary>
    /// Device pixels per terrain cell. The design is <see cref="DesktopScreenTweaks.PixelScale"/> points per cell; on a
    /// denser display that is scaled and rounded to whole pixels, so each cell is an exact block and nothing is resampled.
    /// </summary>
    public int CellPixels { get; private set; } = DesktopScreenTweaks.PixelScale;

    /// <summary>Takes the window's pixel density for this frame; call before sizing the view or the camera.</summary>
    public void SetPixelDensity(float pixelDensity)
    {
        PixelDensity = pixelDensity > 0f ? pixelDensity : 1f;
        CellPixels = Math.Max(1, (int)MathF.Round(DesktopScreenTweaks.PixelScale * PixelDensity));
    }

    private Size _windowPoints;
    private bool _hudShown;

    /// <summary>
    /// The game view in device pixels: the whole window. The HUD is drawn over its bottom corners, so the map shows
    /// through between its two parts and wherever its art is see-through.
    /// </summary>
    /// <param name="windowPixels">Window size in device pixels.</param>
    /// <param name="windowPoints">Window size in points.</param>
    /// <param name="supportsUi">Whether the HUD is shown, which takes its parts out of the play area.</param>
    public Size ComputeViewSize(Size windowPixels, Size windowPoints, bool supportsUi)
    {
        _windowPoints = windowPoints;
        _hudShown = supportsUi;
        return new Size(Math.Max(1, windowPixels.X), Math.Max(1, windowPixels.Y));
    }

    /// <summary>
    /// Whether a point of the view (device pixels) is under the HUD, where aiming and clicks stop so the HUD does not
    /// fire. The gap between the HUD's two parts is play area.
    /// </summary>
    public bool IsUnderHud(float x, float y) =>
        _hudShown && HudLayout.Covers(_windowPoints.X, _windowPoints.Y, PixelDensity, x / PixelDensity, y / PixelDensity);

    public void EnsureHiResBuffer(Size size)
    {
        if (HiResSize == size)
            return;

        HiResSize = size;
    }

    /// <summary>Centres the view on <paramref name="center"/> (cell coordinates), or keeps it when there is none.</summary>
    public void UpdateCamera(PositionF? center, Size terrainSize, int viewW, int viewH, int scale)
    {
        int worldPixelW = terrainSize.X * scale;
        int worldPixelH = terrainSize.Y * scale;

        if (center is PositionF c)
        {
            int centerPx = (int)MathF.Round(c.X * scale);
            int centerPy = (int)MathF.Round(c.Y * scale);
            CamPixelX = centerPx - viewW / 2;
            CamPixelY = centerPy - viewH / 2;
        }

        ClampCamera(worldPixelW, worldPixelH, viewW, viewH);
    }

    public void PanCamera(int dx, int dy, Size terrainSize, int viewW, int viewH, int scale)
    {
        CamPixelX += dx;
        CamPixelY += dy;
        int worldPixelW = terrainSize.X * scale;
        int worldPixelH = terrainSize.Y * scale;
        ClampCamera(worldPixelW, worldPixelH, viewW, viewH);
    }

    /// <summary>
    /// Keeps the view's centre over the world. The view may run past the world's edges by up to half its size, so the
    /// tank stays in the middle of the screen (clear of the HUD in the corners and the panels along the edges) even at
    /// the edge of the map.
    /// </summary>
    private void ClampCamera(int worldPixelW, int worldPixelH, int viewW, int viewH)
    {
        CamPixelX = Math.Clamp(CamPixelX, -viewW / 2, worldPixelW - viewW / 2);
        CamPixelY = Math.Clamp(CamPixelY, -viewH / 2, worldPixelH - viewH / 2);
    }

    public void UpdateNativeContinuousQuality(double frameMs)
    {
        float budget = DesktopScreenTweaks.NativeContinuousRenderBudgetMs;
        int hysteresis = DesktopScreenTweaks.NativeContinuousBudgetHysteresisFrames;
        if (frameMs > budget)
        {
            _nativeOverBudgetFrames++;
            _nativeUnderBudgetFrames = 0;
        }
        else if (frameMs < budget * DesktopScreenTweaks.NativeContinuousBudgetUnderThreshold)
        {
            _nativeUnderBudgetFrames++;
            _nativeOverBudgetFrames = 0;
        }
        else
        {
            _nativeOverBudgetFrames = 0;
            _nativeUnderBudgetFrames = 0;
        }

        if (_nativeOverBudgetFrames >= hysteresis && NativeContinuousSampleCount > DesktopScreenTweaks.NativeContinuousSampleLow)
        {
            NativeContinuousSampleCount = NativeContinuousSampleCount >= DesktopScreenTweaks.NativeContinuousSampleHigh
                ? DesktopScreenTweaks.NativeContinuousSampleMedium
                : DesktopScreenTweaks.NativeContinuousSampleLow;
            _nativeOverBudgetFrames = 0;
            _nativeUnderBudgetFrames = 0;
            Console.WriteLine($"[Render] Native continuous sample count reduced to {NativeContinuousSampleCount}");
        }
        else if (_nativeUnderBudgetFrames >= hysteresis * DesktopScreenTweaks.NativeContinuousRecoveryFramesMultiplier &&
                 NativeContinuousSampleCount < DesktopScreenTweaks.NativeContinuousSampleHigh)
        {
            NativeContinuousSampleCount = NativeContinuousSampleCount <= DesktopScreenTweaks.NativeContinuousSampleLow
                ? DesktopScreenTweaks.NativeContinuousSampleMedium
                : DesktopScreenTweaks.NativeContinuousSampleHigh;
            _nativeOverBudgetFrames = 0;
            _nativeUnderBudgetFrames = 0;
            Console.WriteLine($"[Render] Native continuous sample count increased to {NativeContinuousSampleCount}");
        }
    }
}
