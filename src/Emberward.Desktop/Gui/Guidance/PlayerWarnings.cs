namespace Emberward.Desktop.Gui.Guidance;

using Emberward.Core.Entities.Tanks;

/// <summary>Which warning the banner shows; the hosts size and place it by kind.</summary>
public enum WarningKind { Overheating, Frostbite, Chilled, FireAboard, HullLow, RunningHot, RespawnShield, ThermalViewHint }

/// <summary>The one warning shown at the top of the view.</summary>
/// <param name="Detail">A second line, or null.</param>
/// <param name="Blinks">The title blinks: the tank is being hurt, or soon will be.</param>
public sealed record PlayerWarning(WarningKind Kind, string Title, string? Detail, HintTone Tone, bool Blinks);

/// <summary>
/// Picks the one warning to show, most urgent first: overheating, frostbite, chilled, low shield, running hot, the
/// respawn shield, and once after the first hit a tip about the thermal view. When each is on is decided by Core
/// (<see cref="Tank.IsRunningHot"/>, <see cref="Tank.IsHullLow"/>), which the warning sounds also ask.
/// </summary>
public sealed class PlayerWarnings
{
    private const float ThermalHintSeconds = 8f;

    private bool _runningHotLatched;
    private bool _hullLowLatched;
    private float _thermalHintUntil = -1f;
    private bool _thermalHintShown;

    /// <param name="offerThermalView">
    /// The host has a thermal view that is off now: after the first hit the player is told about it once.
    /// </param>
    /// <param name="now">Seconds on the host's HUD clock.</param>
    public PlayerWarning? Update(Tank player, bool offerThermalView, float now)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.IsDead)
        {
            _runningHotLatched = _hullLowLatched = false;
            return null;
        }

        _runningHotLatched = player.IsRunningHot(_runningHotLatched);
        _hullLowLatched = player.IsHullLow(_hullLowLatched);

        // After the first hit, teach the thermal view once: enemy tunnels glow in it.
        if (!_thermalHintShown && player.HitFlashTicks > 0 && offerThermalView)
        {
            _thermalHintShown = true;
            _thermalHintUntil = now + ThermalHintSeconds;
        }

        if (player.IsOverheating)
            return new(WarningKind.Overheating, "OVERHEATING - SHIELD BURNING",
                "Stop digging and firing, or return to base to cool down", HintTone.Bad, Blinks: true);
        if (player.IsFreezing)
            return new(WarningKind.Frostbite, "FROSTBITE - SHIELD CRACKING",
                "Get away from the frost, or return to base to warm up", HintTone.Info, Blinks: true);
        if (player.SpeedFactor < 0.999f)
            return new(WarningKind.Chilled, $"CHILLED - {(int)MathF.Round(player.SpeedFactor * 100f)}% SPEED", null,
                HintTone.Info, Blinks: false);
        if (player.FireLevel > 0f)
            return new(WarningKind.FireAboard, "FIRE ABOARD - HULL BURNING",
                "The fire heats the tank and the ground around it - return to base to repair the hull", HintTone.Bad, Blinks: true);
        if (_hullLowLatched)
            return new(WarningKind.HullLow, "HULL LOW", "Your base repairs the hull", HintTone.Bad, Blinks: true);
        if (_runningHotLatched)
            return new(WarningKind.RunningHot, "RUNNING HOT",
                "Digging and firing heat the tank - return to base to cool down", HintTone.Warn, Blinks: true);
        if (player.SpawnProtectionTicks > 0)
            return new(WarningKind.RespawnShield, "Respawn shield active", null, HintTone.Info, Blinks: false);
        if (now < _thermalHintUntil)
            return new(WarningKind.ThermalViewHint, "Press H for thermal view - enemy tunnels glow hot", null,
                HintTone.Accent, Blinks: false);
        return null;
    }
}
