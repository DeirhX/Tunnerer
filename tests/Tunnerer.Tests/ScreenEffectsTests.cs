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
        player.TakeDamage(Tweaks.Weapon.BulletDamage, DamageCause.EnemyFire, new VectorF(3f, 0f));
        var levels = effects.Update(player, Frame);

        Assert.Equal(1f, levels.Hit, 3);
        Assert.Equal(-1f, levels.HitFromX, 3);
        Assert.Equal(0f, levels.HitFromY, 3);
        Assert.True(levels.ShakeX > DesktopScreenTweaks.ScreenShakePixels * 0.9f, $"thrown along the shot, got {levels.ShakeX}");

        levels = Run(effects, player, DesktopScreenTweaks.ScreenHitSeconds * 0.5f);
        Assert.InRange(levels.Hit, 0.01f, 0.5f);

        // The flash counter is still running, but it only counts down: no second hit.
        levels = Run(effects, player, DesktopScreenTweaks.ScreenHitSeconds);
        Assert.Equal(0f, levels.Hit);
        Assert.Equal(0f, levels.ShakeX);
        Assert.Equal(0f, levels.ShakeY);
        Assert.False(levels.IsActive);
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
    public void Death_ClearsEveryEffect()
    {
        var player = NewPlayer();
        var effects = new ScreenEffects();
        player.Heat = Tweaks.Tank.HeatSafeMax + 20f;
        Run(effects, player, 1f);
        player.TakeDamage(Tweaks.Weapon.BulletDamage, DamageCause.EnemyFire, new VectorF(0f, 2f));
        Assert.True(effects.Update(player, Frame).IsActive);

        player.TakeDamage(player.Reactor.Health + 10, DamageCause.Overheat);

        Assert.Equal(default, effects.Update(player, Frame));
        Assert.Equal(default, effects.Update(null, Frame));
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
