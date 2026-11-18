namespace Tunnerer.Desktop.Rendering;

using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Tanks;
using Tunnerer.Desktop.Config;

/// <summary>
/// How strongly each whole-screen effect shows this frame (<c>ScreenFxPs.hlsl</c>), all 0 to 1.
/// </summary>
/// <param name="Hit">Just struck by enemy fire: 1 at the hit, fading out.</param>
/// <param name="Overheat">Hotter than the safe maximum: heat haze and a burning edge, closing in as the tank gets
/// hotter until at 1 it covers the whole view.</param>
/// <param name="Freeze">Colder than frostbite: frost creeping in from the edges as the tank gets colder until at 1
/// it covers the whole view.</param>
/// <param name="ShakeX">Horizontal shake offset of the whole view, in device pixels.</param>
/// <param name="ShakeY">Vertical shake offset of the whole view, in device pixels.</param>
/// <param name="HitFromX">Unit direction on screen the last hit came from (x).</param>
/// <param name="HitFromY">Unit direction on screen the last hit came from (y).</param>
public readonly record struct ScreenEffectLevels(
    float Hit,
    float Overheat,
    float Freeze,
    float ShakeX = 0f,
    float ShakeY = 0f,
    float HitFromX = 0f,
    float HitFromY = 0f)
{
    /// <summary>Any effect shows: the renderer runs the screen effect pass only then.</summary>
    public bool IsActive => Hit > 0f || Overheat > 0f || Freeze > 0f;
}

/// <summary>
/// Turns the player tank's state into <see cref="ScreenEffectLevels"/> for each displayed frame, shared by the
/// desktop and browser hosts. When an effect starts and stops is Core's answer (<see cref="Tank.HitFlashTicks"/>,
/// <see cref="Tank.IsAboveSafeHeat"/>, <see cref="Tank.IsBelowFrostbite"/>); this only eases the levels in and out
/// in display time, so they stay smooth between simulation steps.
/// </summary>
public sealed class ScreenEffects
{
    private float _hitAge = float.PositiveInfinity;
    private int _lastHitFlashTicks;
    private float _hitFromX, _hitFromY;
    private float _overheat;
    private float _freeze;

    /// <summary>The levels after the last <see cref="Update"/>.</summary>
    public ScreenEffectLevels Levels { get; private set; }

    /// <param name="player">The local player's tank, or null when there is none (the terrain test).</param>
    /// <param name="deltaSeconds">Display time since the previous frame.</param>
    public ScreenEffectLevels Update(Tank? player, float deltaSeconds)
    {
        float dt = Math.Clamp(deltaSeconds, 0f, 0.1f);
        if (player == null || player.IsDead)
        {
            Reset();
            return Levels;
        }

        // A fresh hit sets the flash counter back up; it only counts down otherwise.
        if (player.HitFlashTicks > _lastHitFlashTicks)
        {
            _hitAge = 0f;
            var v = player.LastHitVelocity;
            float len = v.Length;
            // The shot travelled along v, so it came from -v.
            (_hitFromX, _hitFromY) = len > 1e-3f ? (-v.X / len, -v.Y / len) : (0f, 0f);
        }
        else
        {
            _hitAge += dt;
        }
        _lastHitFlashTicks = player.HitFlashTicks;

        _overheat = Approach(_overheat, OverheatTarget(player), dt,
            DesktopScreenTweaks.ScreenOverheatRiseSeconds, DesktopScreenTweaks.ScreenOverheatFallSeconds);
        _freeze = Approach(_freeze, FreezeTarget(player), dt,
            DesktopScreenTweaks.ScreenFreezeRiseSeconds, DesktopScreenTweaks.ScreenFreezeFallSeconds);

        float hit = HitLevel(_hitAge);
        var (shakeX, shakeY) = Shake(_hitAge, hit);
        Levels = new ScreenEffectLevels(hit, _overheat, _freeze, shakeX, shakeY, _hitFromX, _hitFromY);
        return Levels;
    }

    private void Reset()
    {
        _hitAge = float.PositiveInfinity;
        _lastHitFlashTicks = 0;
        _overheat = _freeze = 0f;
        Levels = default;
    }

    /// <summary>1 at the hit, falling to 0 over <see cref="DesktopScreenTweaks.ScreenHitSeconds"/>, fast at first.</summary>
    private static float HitLevel(float age)
    {
        if (!(age < DesktopScreenTweaks.ScreenHitSeconds))
            return 0f;
        float k = 1f - age / DesktopScreenTweaks.ScreenHitSeconds;
        return k * k;
    }

    /// <summary>
    /// The view jolts away from the shooter and rings back, damped: a cosine along the hit, with a smaller
    /// sideways wobble so it does not read as a plain slide. No randomness, so screenshots repeat.
    /// </summary>
    private (float X, float Y) Shake(float age, float hit)
    {
        if (hit <= 0f)
            return (0f, 0f);
        float amplitude = DesktopScreenTweaks.ScreenShakePixels * hit;
        float along = MathF.Cos(age * DesktopScreenTweaks.ScreenShakeRadiansPerSecond) * amplitude;
        float across = MathF.Sin(age * DesktopScreenTweaks.ScreenShakeRadiansPerSecond * 1.7f) * amplitude * 0.35f;
        // With no direction (a hit with no velocity) shake straight up and down.
        float dx = _hitFromX, dy = _hitFromY;
        if (dx == 0f && dy == 0f)
            dy = 1f;
        // Pushed away from the shooter: the image moves along -from.
        return (-dx * along - dy * across, -dy * along + dx * across);
    }

    /// <summary>
    /// 0 at or below the safe maximum; from <see cref="SeverityFloor"/> just past it to 1 at the top of the heat gauge,
    /// where the effect covers the whole view.
    /// </summary>
    private static float OverheatTarget(Tank player)
    {
        if (!player.IsAboveSafeHeat && !player.IsOverheating)
            return 0f;
        float excess = (player.Heat - Tweaks.Tank.HeatSafeMax) / (Tank.HeatGaugeMax - Tweaks.Tank.HeatSafeMax);
        return Severity(excess);
    }

    /// <summary>
    /// 0 at or above frostbite; from <see cref="SeverityFloor"/> just below it to 1 at the bottom of the heat gauge,
    /// where the frost covers the whole view.
    /// </summary>
    private static float FreezeTarget(Tank player)
    {
        if (!player.IsBelowFrostbite && !player.IsFreezing)
            return 0f;
        float deficit = (Tweaks.Tank.FrostbiteTemperature - player.Heat) /
                        (Tweaks.Tank.FrostbiteTemperature - Tank.HeatGaugeMin);
        return Severity(deficit);
    }

    /// <summary>The level an effect shows as soon as it starts: a thin edge, so crossing the limit is seen at once.</summary>
    private const float SeverityFloor = 0.12f;

    /// <summary>How far past the limit the tank is (0 at the limit, 1 at the end of the heat gauge), as a level.</summary>
    private static float Severity(float pastLimit) =>
        SeverityFloor + (1f - SeverityFloor) * Math.Clamp(pastLimit, 0f, 1f);

    /// <summary>Moves <paramref name="value"/> toward <paramref name="target"/> at a full 0-1 sweep per the given seconds.</summary>
    private static float Approach(float value, float target, float dt, float riseSeconds, float fallSeconds)
    {
        if (value < target)
            return MathF.Min(target, value + dt / riseSeconds);
        return MathF.Max(target, value - dt / fallSeconds);
    }
}
