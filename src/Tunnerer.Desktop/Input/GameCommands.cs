namespace Tunnerer.Desktop.Input;

public enum GameCommand
{
    ToggleThermalRegionsOverlay,
    ToggleHeatOverlay,
    TogglePostVignette,
    TogglePostTerrainAux,
    TogglePostTerrainHeat,
    TogglePostTankGlow,
    TogglePostNativeTerrainSmoothing,
    TogglePostNativeEdgeCurving,
    RequestScreenshot,
    TogglePostPassOverlay,
    ToggleInputRecording,
    /// <summary>Starts a new match; only acts once the current match is over (Enter, or a command script).</summary>
    RestartMatch,
    TogglePause,
    ToggleControls,
}
