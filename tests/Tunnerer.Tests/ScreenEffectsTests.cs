using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities;
using Tunnerer.Core.Entities.Tanks;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Config;
using Tunnerer.Desktop.Rendering;

namespace Tunnerer.Tests;

/// <summary>
/// The whole-screen effects (<see cref="ScreenEffects"/>): when each starts and stops, how it eases, and that the
/// constant block hands the levels to <c>ScreenFxPs</c>.
/// </summary>
public class ScreenEffectsTests
{
    private const float Frame = 1f / 60f;

    private static Tank NewPlayer() =>
        TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false).TankList.Tanks[0];

    /// <summary>Enemy fire hurts <paramref name="tank"/> in a step, travelling along <paramref name="velocity"/>.</summary>
    private static void HitInAStep(ScreenEffects effects, Tank tank, Tank player, VectorF velocity)
    {
        tank.TakeDamage(Tweaks.Weapon.BulletDamage, DamageCause.EnemyFire, velocity);
        effects.AfterStep([new StepEvent(StepEventKind.TankHit, tank.Position, tank.Color)], player);
    }

    private static ScreenEffectLevels Run(ScreenEffects effects, Tank player, float seconds)
    {
        ScreenEffectLevels levels = effects.Levels;
        for (float t = 0f; t < seconds; t += Frame)
            levels = effects.Update(player, Frame);
        return levels;
    }

    [Fact]
    public void ATankAtEase_ShowsNothing()
    {
        var player = NewPlayer();
        player.Heat = 20f;

        var levels = Run(new ScreenEffects(), player, 1f);

        Assert.False(levels.IsActive);
        Assert.Equal(default, levels);
    }

    [Fact]
    public void AHit_ShakesTheViewAlongTheShot_ThenFades()
    {
        var player = NewPlayer();
        var effects = new ScreenEffects();
        effects.Update(player, Frame);

        // The shot travelled toward +x, so it came from -x.
        HitInAStep(effects, player, player, new VectorF(3f, 0f));
        var levels = effects.Update(player, Frame);

        Assert.Equal(1f, levels.Hit, 3);
        Assert.Equal(-1f, levels.HitFromX, 3);
        Assert.Equal(0f, levels.HitFromY, 3);
        Assert.True(levels.ShakeX > DesktopScreenTweaks.ScreenShakePixels * 0.9f, $"thrown along the shot, got {levels.ShakeX}");

        levels = Run(effects, player, DesktopScreenTweaks.ScreenHitSeconds * 0.5f);
        Assert.InRange(levels.Hit, 0.01f, 0.5f);

        // Steps without a hit start nothing new.
        effects.AfterStep([], player);
        levels = Run(effects, player, DesktopScreenTweaks.ScreenHitSeconds);
        Assert.Equal(0f, levels.Hit);
        Assert.Equal(0f, levels.ShakeX);
        Assert.Equal(0f, levels.ShakeY);
        Assert.False(levels.IsActive);
    }

    [Fact]
    public void AHitOnTheNextStep_StartsAgain_AndAHitOnAnotherTankDoesNothing()
    {
        var world = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false);
        var (player, other) = (world.TankList.Tanks[0], world.TankList.Tanks[1]);
        var effects = new ScreenEffects();

        HitInAStep(effects, other, player, new VectorF(0f, 3f));
        Assert.False(effects.Update(player, Frame).IsActive);

        HitInAStep(effects, player, player, new VectorF(3f, 0f));
        effects.Update(player, Frame);
        float faded = effects.Update(player, 1f / Tweaks.Perf.TargetFps).Hit;
        Assert.True(faded < 1f);

        // The next tick hits again, from below: the shake starts over, along the new shot.
        HitInAStep(effects, player, player, new VectorF(0f, -3f));
        var levels = effects.Update(player, Frame);
        Assert.Equal(1f, levels.Hit, 3);
        Assert.Equal((0f, 1f), (levels.HitFromX, levels.HitFromY));
    }

    [Fact]
    public void Overheating_GrowsWithTheHeat_AndCoolingClearsIt()
    {
        var player = NewPlayer();
        var effects = new ScreenEffects();
        player.Heat = Tweaks.Tank.HeatSafeMax + 1f;

        var levels = effects.Update(player, Frame);
        Assert.InRange(levels.Overheat, 0.001f, 0.1f); // eases in, does not jump

        // Just past the limit: a thin edge.
        float justPast = Run(effects, player, DesktopScreenTweaks.ScreenOverheatRiseSeconds).Overheat;
        Assert.InRange(justPast, 0.05f, 0.3f);
        Assert.Equal(0f, effects.Levels.Freeze);

        // Hotter shows more, and the top of the heat gauge covers the whole view.
        player.Heat = (Tweaks.Tank.HeatSafeMax + Tank.HeatGaugeMax) / 2f;
        float halfway = Run(effects, player, DesktopScreenTweaks.ScreenOverheatRiseSeconds).Overheat;
        Assert.InRange(halfway, justPast + 0.2f, 0.8f);
        player.Heat = Tank.HeatGaugeMax + 10f;
        levels = Run(effects, player, DesktopScreenTweaks.ScreenOverheatRiseSeconds);
        Assert.Equal(1f, levels.Overheat, 3);

        player.Heat = Tweaks.Tank.HeatSafeMax - 1f;
        levels = Run(effects, player, DesktopScreenTweaks.ScreenOverheatFallSeconds * 1.1f);
        Assert.Equal(0f, levels.Overheat);
    }

    [Fact]
    public void Frostbite_GrowsWithTheCold_AndChillAloneDoesNot()
    {
        var player = NewPlayer();
        var effects = new ScreenEffects();

        player.Heat = Tweaks.Tank.FrostbiteTemperature + 1f;
        var levels = Run(effects, player, DesktopScreenTweaks.ScreenFreezeRiseSeconds);
        Assert.Equal(0f, levels.Freeze);

        player.Heat = Tweaks.Tank.FrostbiteTemperature - 1f;
        float justPast = Run(effects, player, DesktopScreenTweaks.ScreenFreezeRiseSeconds).Freeze;
        Assert.InRange(justPast, 0.05f, 0.3f);
        Assert.Equal(0f, effects.Levels.Overheat);

        player.Heat = Tank.HeatGaugeMin;
        levels = Run(effects, player, DesktopScreenTweaks.ScreenFreezeRiseSeconds);
        Assert.Equal(1f, levels.Freeze, 3);

        player.Heat = 0f;
        levels = Run(effects, player, DesktopScreenTweaks.ScreenFreezeFallSeconds * 1.1f);
        Assert.Equal(0f, levels.Freeze);
    }

    [Fact]
    public void Death_FadesEveryEffectOut_AndNoTankClearsThemAtOnce()
    {
        var player = NewPlayer();
        var effects = new ScreenEffects();
        player.Heat = Tweaks.Tank.HeatSafeMax + 20f;
        Run(effects, player, 1f);
        HitInAStep(effects, player, player, new VectorF(0f, 2f));
        float before = effects.Update(player, Frame).Overheat;

        player.TakeDamage(player.Reactor.Health + 10, DamageCause.Overheat);

        float after = effects.Update(player, Frame).Overheat;
        Assert.InRange(after, before * 0.9f, before);
        var levels = Run(effects, player, DesktopScreenTweaks.ScreenOverheatFallSeconds * 1.1f);
        Assert.False(levels.IsActive);

        // With no tank at all (the terrain test) nothing is left to fade.
        var alive = NewPlayer();
        alive.Heat = Tweaks.Tank.HeatSafeMax + 20f;
        Assert.True(Run(effects, alive, 1f).IsActive);
        Assert.Equal(default, effects.Update(null, Frame));
    }

    [Fact]
    public void AStandingWorld_HoldsTheEffectsWhereTheyAre()
    {
        var player = NewPlayer();
        var effects = new ScreenEffects();
        player.Heat = Tweaks.Tank.HeatSafeMax + 20f;
        HitInAStep(effects, player, player, new VectorF(3f, 0f));
        effects.Update(player, Frame);
        var held = effects.Update(player, Frame);

        for (int i = 0; i < 60; i++)
            Assert.Equal(held, effects.Update(player, 0f));
    }

    [Fact]
    public void TheCrosshair_IsDrawnWhereTheShakenViewShowsTheAim()
    {
        Assert.Equal((120f, -40f), default(ScreenEffectLevels).ShownFromCentre(120f, -40f, 1080f));

        // At a hit's height the view is zoomed in and thrown 10 pixels right (20 on a view twice as high).
        var levels = new ScreenEffectLevels(1f, 0f, 0f, ShakeX: 10f);
        float zoom = 1f - DesktopScreenTweaks.ScreenHitZoom;
        var (x, y) = levels.ShownFromCentre(100f, 50f, 2160f);
        Assert.Equal((100f + 20f) / zoom, x, 3);
        Assert.Equal(50f / zoom, y, 3);

        // ScreenFxPs samples the image at uv * zoom - shake (from the centre): the crosshair lands on the aim.
        Assert.Equal(100f, x * zoom - 20f, 3);
    }

    [Fact]
    public void TheConstantBlock_CarriesTheLevels()
    {
        var levels = new ScreenEffectLevels(0.5f, 0.25f, 0.75f, 3f, -4f, 0f, 1f);
        var upload = new GamePixelsUpload(new RenderView(new Size(64, 32), new Size(10, 10), default, 2), default,
            default, default, default, default, ScreenEffects: levels);

        var block = PostParamsBlock.Build(upload, 0f);

        Assert.Equal(1f, block.ScreenFxActive);
        Assert.Equal((0.5f, 0.25f, 0.75f), (block.ScreenFxHit, block.ScreenFxOverheat, block.ScreenFxFreeze));
        Assert.Equal((3f, -4f, 0f, 1f), (block.ScreenShakeX, block.ScreenShakeY, block.ScreenHitFromX, block.ScreenHitFromY));
        Assert.Equal(0f, PostParamsBlock.Build(upload with { ScreenEffects = default }, 0f).ScreenFxActive);
    }
}
