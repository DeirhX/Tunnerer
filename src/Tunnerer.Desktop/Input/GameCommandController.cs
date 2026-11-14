namespace Tunnerer.Desktop.Input;

using static SDL3.SDL;
using Tunnerer.Desktop.Config;

public sealed class GameCommandController
{
    private bool _showHeatDebugOverlay;
    private bool _showPostPassOverlay;
    private PostProcessPassFlags _enabledPostPasses = PostProcessPassFlags.All & ~PostProcessPassFlags.ThermalRegions;

    public bool ShowHeatDebugOverlay => _showHeatDebugOverlay;
    public bool ShowPostPassOverlay => _showPostPassOverlay;
    public PostProcessPassFlags EnabledPostPasses => _enabledPostPasses;

    public static bool TryTranslateHotkey(Scancode scancode, bool shiftHeld, out GameCommand command)
        => TryTranslateHotkey(scancode, shiftHeld, debugKeysEnabled: true, out command);

    /// <summary>
    /// Maps a key to a command. The shader-pass toggles (1-7, Shift+1/2) only work while the debug overlay (F11) is
    /// open, so a stray key press in normal play cannot switch off parts of the renderer; H toggles the thermal view.
    /// </summary>
    public static bool TryTranslateHotkey(Scancode scancode, bool shiftHeld, bool debugKeysEnabled, out GameCommand command)
    {
        if (scancode == Scancode.H)
        {
            command = GameCommand.ToggleHeatOverlay;
            return true;
        }

        if (!debugKeysEnabled && scancode is >= Scancode.Alpha1 and <= Scancode.Alpha9)
        {
            command = default;
            return false;
        }

        if (shiftHeld)
        {
            switch (scancode)
            {
                case Scancode.Alpha1:
                    command = GameCommand.ToggleHeatOverlay;            // #8
                    return true;
                case Scancode.Alpha2:
                    command = GameCommand.ToggleThermalRegionsOverlay;   // #9
                    return true;
            }
        }

        switch (scancode)
        {
            case Scancode.F9:
                command = GameCommand.ToggleHeatOverlay;
                return true;
            case Scancode.Alpha1:
                command = GameCommand.TogglePostVignette;
                return true;
            case Scancode.Alpha3:
                command = GameCommand.TogglePostTerrainAux;
                return true;
            case Scancode.Alpha4:
                command = GameCommand.TogglePostTankGlow;
                return true;
            case Scancode.Alpha5:
                command = GameCommand.TogglePostNativeTerrainSmoothing;
                return true;
            case Scancode.Alpha6:
                command = GameCommand.TogglePostTerrainHeat;
                return true;
            case Scancode.Alpha7:
                command = GameCommand.TogglePostNativeEdgeCurving;
                return true;
            case Scancode.F10:
                command = GameCommand.RequestScreenshot;
                return true;
            case Scancode.F11:
                command = GameCommand.TogglePostPassOverlay;
                return true;
            case Scancode.F12:
                command = GameCommand.ToggleInputRecording;
                return true;
            default:
                command = default;
                return false;
        }
    }

    public void Execute(
        GameCommand command,
        string source,
        Action<string?> requestScreenshot,
        Action<string> toggleInputRecording)
    {
        switch (command)
        {
            case GameCommand.ToggleThermalRegionsOverlay:
                TogglePassWithLog(PostProcessPassFlags.ThermalRegions, "Thermal regions", source);
                return;
            case GameCommand.ToggleHeatOverlay:
                ToggleWithLog(ref _showHeatDebugOverlay, "Debug", "Heat overlay", source);
                return;
            case GameCommand.TogglePostVignette:
                TogglePassWithLog(PostProcessPassFlags.Vignette, "Vignette", source);
                return;
            case GameCommand.TogglePostTerrainAux:
                TogglePassWithLog(PostProcessPassFlags.TerrainAux, "Terrain texture", source);
                return;
            case GameCommand.TogglePostTerrainHeat:
                TogglePassWithLog(PostProcessPassFlags.TerrainHeat, "Terrain heat", source);
                return;
            case GameCommand.TogglePostTankGlow:
                TogglePassWithLog(PostProcessPassFlags.TankGlow, "Tank glow", source);
                return;
            case GameCommand.TogglePostNativeTerrainSmoothing:
                TogglePassWithLog(PostProcessPassFlags.NativeTerrainSmoothing, "Native terrain smoothing", source);
                return;
            case GameCommand.TogglePostNativeEdgeCurving:
                TogglePassWithLog(PostProcessPassFlags.NativeEdgeCurving, "Native edge curving", source);
                return;
            case GameCommand.RequestScreenshot:
                requestScreenshot("postps");
                Console.WriteLine($"[Debug] Screenshot requested [source={source}].");
                return;
            case GameCommand.TogglePostPassOverlay:
                ToggleWithLog(ref _showPostPassOverlay, "Debug", "Post pass overlay", source);
                return;
            case GameCommand.ToggleInputRecording:
                toggleInputRecording(source);
                return;
            default:
                return;
        }
    }

    private static void ToggleWithLog(ref bool flag, string group, string label, string source)
    {
        flag = !flag;
        Console.WriteLine($"[{group}] {label}: {(flag ? "ON" : "OFF")} [source={source}]");
    }

    private void TogglePassWithLog(PostProcessPassFlags pass, string label, string source)
    {
        _enabledPostPasses ^= pass;
        bool enabled = (_enabledPostPasses & pass) != 0;
        Console.WriteLine($"[PostPs] {label}: {(enabled ? "ON" : "OFF")} [source={source}]");
    }
}
