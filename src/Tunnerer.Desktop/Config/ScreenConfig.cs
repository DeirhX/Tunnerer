namespace Tunnerer.Desktop.Config;

using Tunnerer.Core.Config;
using Tunnerer.Core.Types;

/// <summary>
/// Injectable configuration for desktop rendering and screen layout.
/// Tests can construct custom instances; production code uses <see cref="FromTweaks"/>.
/// </summary>
public readonly record struct ScreenConfig
{
    public Size WindowSize { get; init; }
    public Size RenderSurfaceSize { get; init; }
    public int PixelScale { get; init; }
    public int NativeContinuousSampleLow { get; init; }
    public int NativeContinuousSampleMedium { get; init; }
    public int NativeContinuousSampleHigh { get; init; }
    public float NativeContinuousRenderBudgetMs { get; init; }
    public int NativeContinuousBudgetHysteresisFrames { get; init; }
    public float NativeContinuousBudgetUnderThreshold { get; init; }
    public int NativeContinuousRecoveryFramesMultiplier { get; init; }

    public static ScreenConfig FromTweaks() => new()
    {
        WindowSize = Tweaks.Screen.WindowSize,
        RenderSurfaceSize = Tweaks.Screen.RenderSurfaceSize,
        PixelScale = Tweaks.Screen.PixelScale,
        NativeContinuousSampleLow = Tweaks.Screen.NativeContinuousSampleLow,
        NativeContinuousSampleMedium = Tweaks.Screen.NativeContinuousSampleMedium,
        NativeContinuousSampleHigh = Tweaks.Screen.NativeContinuousSampleHigh,
        NativeContinuousRenderBudgetMs = Tweaks.Screen.NativeContinuousRenderBudgetMs,
        NativeContinuousBudgetHysteresisFrames = Tweaks.Screen.NativeContinuousBudgetHysteresisFrames,
        NativeContinuousBudgetUnderThreshold = Tweaks.Screen.NativeContinuousBudgetUnderThreshold,
        NativeContinuousRecoveryFramesMultiplier = Tweaks.Screen.NativeContinuousRecoveryFramesMultiplier,
    };
}
