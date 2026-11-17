namespace Tunnerer.Tests;

using System.Text.Json;
using Tunnerer.Browser.Client;
using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Bases;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Entities.Projectiles;
using Tunnerer.Core.Input;
using Tunnerer.Core.Types;
using System.Runtime.InteropServices;
using Tunnerer.Desktop.Audio;
using Tunnerer.Desktop.Config;
using Tunnerer.Desktop.Gui;
using Tunnerer.Desktop.Rendering;
using Tunnerer.Desktop.Rendering.Entities;

public class BrowserSessionTests
{
    [Fact]
    public void Sounds_AreTheDesktopDirectors_HandedOverOnce_WithEveryLoop()
    {
        var session = new BrowserSession(1234);
        for (int i = 0; i < 3; i++)
            session.Advance(new ControllerOutput { MoveSpeed = new Offset(0, 1) });
        session.Advance(new ControllerOutput { BuildPrimary = true });

        var sounds = BrowserFrameBuilder.Capture(session, includeEvents: true).Sounds;

        // The player's own build, heard where it happens: at full gain, centred, as the desktop plays it.
        Assert.Contains(new SoundPlay(nameof(SoundEffect.BuildRelay), AudioTweaks.BuildGain, 0f, 1f), sounds.Play);
        Assert.Equal(SoundDirector.MaxTrackedTanks * 3 + 1, sounds.Loops.Length);
        Assert.Equal(nameof(SoundEffect.EngineLoop), sounds.Loops[0].Effect);
        Assert.Equal(nameof(SoundEffect.RechargeLoop), sounds.Loops[^1].Effect);
        Assert.True(sounds.Loops[0].Gain > 0f, "The player's engine runs.");
        Assert.Equal(0f, sounds.Loops[3 * 3].Gain);   // a fourth tank that is not there
        // A frame without a step starts nothing again; the loops stay where they are.
        var again = BrowserFrameBuilder.Capture(session, includeEvents: false).Sounds;
        Assert.Empty(again.Play);
        Assert.Equal(sounds.Loops, again.Loops);
    }

    [Fact]
    public void SoundBank_PacksEveryDesktopEffect_OneAfterAnother()
    {
        var bank = new BrowserSoundBank();
        var effects = bank.Layout.Effects;

        Assert.Equal(AudioTweaks.SampleRate, bank.Layout.SampleRate);
        Assert.Equal(Enum.GetNames<SoundEffect>(), effects.Select(e => e.Effect));
        Assert.Equal(0, effects[0].Offset);
        for (int i = 1; i < effects.Length; i++)
            Assert.Equal(effects[i - 1].Offset + effects[i - 1].Length, effects[i].Offset);
        Assert.Equal(bank.Samples.Length, effects[^1].Offset + effects[^1].Length);
        Assert.All(effects, e => Assert.Equal(SoundBank.IsLoop(Enum.Parse<SoundEffect>(e.Effect)), e.Loop));
        Assert.Equal(bank.Samples.Length * 4, bank.ToBytes().Length);
        var desktop = new SoundBank()[SoundEffect.Fire];
        var fire = effects.Single(e => e.Effect == nameof(SoundEffect.Fire));
        Assert.Equal(desktop, bank.Samples.AsSpan(fire.Offset, fire.Length).ToArray());
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void BrowserPresentation_LeavesTheSimulationUnchanged_AndUsesCoreBuildAndUpgradeRules()
    {
        var session = new BrowserSession(1234);
        var player = session.Player;
        var home = player.Base!;
        ulong before = WorldStateHash.Compute(session.World);
        session.Scene.Render(1280, 720, 6, 1, 0, 0);
        var frame = BrowserFrameBuilder.Capture(session, includeEvents: false);
        var json = JsonSerializer.Serialize(frame, BrowserJsonContext.Default.BrowserFrame);

        Assert.Equal(before, WorldStateHash.Compute(session.World));
        Assert.Equal(2, session.World.TankList.Count);
        Assert.Equal(1, session.World.Settings.ThermalMaxWorkers);
        Assert.Equal(player.Position.X, frame.Player.Position.X);
        Assert.Contains("\"tickRate\":24", json, StringComparison.Ordinal);
        Assert.Empty(frame.Sounds.Play);
        var desktopEntities = new EntityLayerBuilder();
        desktopEntities.CaptureStep(session.World, 0);
        desktopEntities.Build(session.Scene.View, 1, 0, false, 0, session.Vision, session.Radar);
        Assert.Equal(MemoryMarshal.AsBytes(desktopEntities.Vertices.AsSpan(0, desktopEntities.VertexCount)).ToArray(), session.Scene.Vertices.ToArray());
        Assert.Equal(Marshal.SizeOf<PostParamsBlock>(), session.Scene.Uniforms.Length);

        // The first packet carries the four world-sized textures; a frame in which nothing changed carries none.
        int[] header = MemoryMarshal.Cast<byte, int>(session.Scene.Packet.AsSpan(0, BrowserScene.HeaderInts * 4)).ToArray();
        int mapBytes = BrowserSession.MapSize.Area * 4;
        for (int t = 0; t < BrowserTerrain.TextureCount; t++)
            Assert.Equal([0, 0, BrowserSession.MapSize.X, BrowserSession.MapSize.Y], header[(5 + 5 * t)..(9 + 5 * t)]);
        Assert.Equal(header[4 + 5 * 3] + mapBytes, session.Scene.Packet.Count);
        session.Scene.Render(1280, 720, 6, 1, 0, 0);
        header = MemoryMarshal.Cast<byte, int>(session.Scene.Packet.AsSpan(0, BrowserScene.HeaderInts * 4)).ToArray();
        for (int t = 0; t < BrowserTerrain.TextureCount; t++)
            Assert.Equal(0, header[7 + 5 * t]);
        Assert.Equal(BrowserScene.HeaderInts * 4 + header[1] + header[3], session.Scene.Packet.Count);
        Assert.Equal(desktopEntities.VertexCount * Marshal.SizeOf<EntityVertex>(), header[3]);

        int upgradeCost = home.Upgrades.NextCost(BaseUpgrade.Radiator);
        int store = home.Store.Ore;
        session.Advance(new ControllerOutput { Upgrade = 1 });
        Assert.Equal(1, home.Upgrades.Level(BaseUpgrade.Radiator));
        Assert.Equal(store - upgradeCost, home.Store.Ore);
        session.Advance(default);
        store = home.Store.Ore;
        session.Advance(new ControllerOutput { BuildPrimary = true });
        var built = BrowserFrameBuilder.Capture(session, includeEvents: true);
        Assert.True(player.IsCarryingMachine);
        Assert.Equal(store - frame.Builds[0].Cost, home.Store.Ore);
        Assert.Contains(built.Sounds.Play, e => e.Effect == nameof(SoundEffect.BuildRelay) && e.Gain == AudioTweaks.BuildGain);

        Position start = player.Position;
        for (int i = 0; i < 30; i++)
            session.Advance(new ControllerOutput { MoveSpeed = new Offset(0, -1) });
        Assert.True(player.Position.Y < start.Y);
        session.Scene.Render(int.MaxValue, int.MaxValue, 1, 1, 0, 1);
        Rect visible = session.Scene.View.VisibleCells();
        Assert.True(visible.Width <= 640);
        Assert.True(visible.Height <= 400);
        // A live projectile in visible open ground must become the same hot-shot quad used by desktop.
        session.World.Projectiles.Add(Projectile.CreateBullet(home.Position, new VectorF(1, 0), player.Color));
        session.Advance(default);
        session.Scene.Render(1280, 720, 6, 1, 0, 2);
        Assert.Contains(MemoryMarshal.Cast<byte, EntityVertex>(session.Scene.Vertices).ToArray(),
            v => v.Kind == EntityShape.KindHotShot);
    }

    [Fact]
    public void Frame_CarriesTheDesktopGuidance_AndItFollowsTheMatch()
    {
        var session = new BrowserSession(1234);
        var home = session.Player.Base!;

        var start = BrowserFrameBuilder.Capture(session, includeEvents: false).Guidance;
        Assert.NotNull(start.Tutorial);
        Assert.Equal((1, "Leave the base"), (start.Tutorial.Number, start.Tutorial.Title));
        Assert.NotNull(start.Tutorial.Target);
        Assert.Equal(session.Guidance.Hint.Text, start.Hint.Text);
        Assert.StartsWith("WASD to drive", start.Hint.Text, StringComparison.Ordinal);
        Assert.Equal(nameof(HintTone.Info), start.Hint.Tone);

        // Out of the base, the tutorial moves on after the next step, as the desktop's does.
        session.Player.Position = home.Position + new Offset(0, Tweaks.Base.BaseSize / 2 + 8);
        session.Advance(default);
        var outside = BrowserFrameBuilder.Capture(session, includeEvents: false);
        Assert.Equal("Mine emberite", outside.Guidance.Tutorial?.Title);
        Assert.Contains("\"guidance\":{\"tutorial\":", JsonSerializer.Serialize(outside, BrowserJsonContext.Default.BrowserFrame),
            StringComparison.Ordinal);
    }
}

public class BrowserSessionThreadsTests
{
    /// <summary>
    /// What lets the browser pick its runtime per device: the world does not depend on how many threads step its
    /// heat. tools/browser/smoke.mjs asks the same of the two WebAssembly runtimes.
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")]
    public void HeatWorkers_DoNotChangeTheWorld()
    {
        // Fewer steps than a profile report covers, so its count of steps solved on threads is still there to read.
        static (ulong World, int ParallelSteps, SimulationSettings Settings) Play(int heatWorkers)
        {
            var session = new BrowserSession(1234, heatWorkers);
            for (int i = 0; i < 90; i++)
                session.Advance(default);
            return (session.Fingerprint(), session.World.Profile.CooldownParallelFrames, session.World.Settings);
        }

        var single = Play(1);
        var threaded = Play(4);
        Assert.Equal(0, single.ParallelSteps);
        Assert.True(threaded.ParallelSteps > 0, "The match must give the heat solver enough to split.");
        Assert.Equal(4, threaded.Settings.ThermalMaxWorkers);
        Assert.Equal(Tweaks.World.ThermalParallelTileThreshold, threaded.Settings.ThermalParallelTileThreshold);
        Assert.Equal(single.World, threaded.World);
    }
}
