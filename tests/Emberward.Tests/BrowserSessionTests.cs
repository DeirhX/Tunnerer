namespace Emberward.Tests;

using System.Text.Json;
using Emberward.Browser.Client;
using Emberward.Core;
using Emberward.Core.Config;
using Emberward.Core.Entities.Bases;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Entities.Projectiles;
using Emberward.Core.Input;
using Emberward.Core.Types;
using System.Runtime.InteropServices;
using Emberward.Desktop.Audio;
using Emberward.Desktop.Config;
using Emberward.Desktop.Gui;
using Emberward.Desktop.Presentation;
using Emberward.Desktop.Rendering;
using Emberward.Desktop.Rendering.Entities;

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
        Assert.Equal(SoundDirector.MaxTrackedTanks * 5 + 1, sounds.Loops.Length);
        Assert.Equal(nameof(SoundEffect.EngineLoop), sounds.Loops[0].Effect);
        Assert.Equal(nameof(SoundEffect.RechargeLoop), sounds.Loops[^1].Effect);
        Assert.True(sounds.Loops[0].Gain > 0f, "The player's engine runs.");
        Assert.Equal(0f, sounds.Loops[3 * 5].Gain);   // a fourth tank that is not there
        // A frame without a step starts nothing again; the loops stay where they are.
        var again = BrowserFrameBuilder.Capture(session, includeEvents: false).Sounds;
        Assert.Empty(again.Play);
        Assert.Equal(sounds.Loops, again.Loops);
    }

}

public class BrowserSessionStatusTests
{
    [Fact]
    public void Status_CarriesTheDesktopsHeatColour_UpgradeTracks_AndNoHitAtTheStart()
    {
        var session = new BrowserSession(1234);
        var status = BrowserFrameBuilder.Capture(session, includeEvents: false);

        // The desktop's ambient green (HudLogicTests: 0xFF66E566 in ImGui's ABGR).
        Assert.Equal("#66e566", BrowserFrameBuilder.HexColor(HeatScale.ColorRgb(0f)));
        Assert.Equal(BrowserFrameBuilder.HexColor(HeatScale.ColorRgb(session.Player.Heat)), status.Player.HeatColor);
        Assert.Equal(HeatScale.Fraction(session.Player.Heat), status.Player.HeatGauge);
        Assert.Equal((0f, 0f, 0f), (status.Player.HitFlash, status.Player.HitFromX, status.Player.HitFromY));
        Assert.Equal(["Base", "Base", "Base", "Base", "Base", "Tank", "Tank"], status.Upgrades.Select(u => u.Track));
        Assert.All(status.Upgrades, u => Assert.Equal(BaseUpgrades.MaxLevel, u.MaxLevel));
    }

}

public class BrowserSoundBankTests
{
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

}

public class BrowserPresentationTests
{
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
        int mapBytes = session.MapSize.Area * 4;
        for (int t = 0; t < BrowserTerrain.TextureCount; t++)
            Assert.Equal([0, 0, session.MapSize.X, session.MapSize.Y], header[(5 + 5 * t)..(9 + 5 * t)]);
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
        session.World.Projectiles.Add(Projectile.CreateBullet(home.SpawnPosition, new VectorF(1, 0), player.Color));
        session.Advance(default);
        session.Scene.Render(1280, 720, 6, 1, 0, 2);
        Assert.Contains(MemoryMarshal.Cast<byte, EntityVertex>(session.Scene.Vertices).ToArray(),
            v => v.Kind == EntityShape.KindHotShot);
    }

}

public class BrowserSessionArtStripTests
{
    [Fact]
    public void Frame_CarriesWhatTheDesktopsArtStripShows_AndTheDialOnlyWhenAsked()
    {
        var session = new BrowserSession(1234);
        var player = session.Player;

        var first = BrowserFrameBuilder.Capture(session, includeEvents: false, includeDial: true);
        Assert.Null(BrowserFrameBuilder.Capture(session, includeEvents: false).Dial);
        var dial = first.Dial!;
        Assert.Equal(HeatScale.DialBands, dial.Bands.Length);
        // Each band in its middle's colour, as HeatGauge draws them.
        Assert.Equal(BrowserFrameBuilder.HexColor(HeatScale.ColorRgb(HeatScale.At(0.5f / HeatScale.DialBands))), dial.Bands[0]);
        Assert.Equal((HeatScale.Min, HeatScale.Max), (dial.Min, dial.Max));
        Assert.InRange(dial.Ambient, 0f, dial.Safe);

        var p = first.Player;
        Assert.Equal(player.Reactor.Health, p.Health);
        Assert.Equal((HeatScale.Frozen(player.Heat), HeatScale.Boil(player.Heat)), (p.Frozen, p.Boil));
        Assert.Equal(BuildSlotView.OreLine(player), p.OreLine);
        Assert.Equal(-1, p.OrePileFrame);
        Assert.Equal("ORE", p.OreLine);
        // Each machine in its desktop slot, as the desktop's BuildPanel draws it.
        Assert.Equal([0, 1, 2, 3, 4], first.Builds.Select(b => b.Slot).Order());
        foreach (var b in first.Builds)
        {
            var machine = MachinePresentations.All.Single(m => m.Type.ToString() == b.Name);
            var view = BuildSlotView.Of(machine, player.QuoteBuild(machine.Type), player.CarriedMachineType);
            Assert.Equal((view.Machine.BuildSlot, view.State.ToString(), view.Label, view.Caption), (b.Slot, b.State, b.Label, b.Caption));
        }

        session.Advance(new ControllerOutput { BuildPrimary = true });
        var carrying = BrowserFrameBuilder.Capture(session, includeEvents: false).Builds;
        Assert.Equal(nameof(BuildSlotState.Carrying), carrying.Single(b => b.Key == 1).State);
        Assert.All(carrying.Where(b => b.Key != 1), b => Assert.Equal(nameof(BuildSlotState.Blocked), b.State));

        player.Hold.Add(1);
        Assert.Equal(0, BrowserFrameBuilder.Capture(session, includeEvents: false).Player.OrePileFrame);
        player.Hold.Ore = player.OreHoldCapacity / 2;
        Assert.Equal(19, BrowserFrameBuilder.Capture(session, includeEvents: false).Player.OrePileFrame);
        player.Hold.Add(player.OreHoldCapacity);
        Assert.Equal(39, BrowserFrameBuilder.Capture(session, includeEvents: false).Player.OrePileFrame);
    }

}

public class BrowserSessionThermalViewTests
{
    [Fact]
    public void ThermalView_TintsTheFrame_AndLeavesTheWorldAlone()
    {
        var session = new BrowserSession(1234);
        static float Overlay(BrowserSession s) => MemoryMarshal.Read<PostParamsBlock>(s.Scene.Uniforms).HeatDebugOverlay;

        session.Scene.Render(1280, 720, 6, 1, 0, 0);
        Assert.Equal(0f, Overlay(session));
        ulong before = WorldStateHash.Compute(session.World);
        session.ThermalView = true;
        session.Scene.Render(1280, 720, 6, 1, 0, 0);
        Assert.Equal(1f, Overlay(session));
        Assert.Equal(before, WorldStateHash.Compute(session.World));
    }

}

public class BrowserSessionGuidanceTests
{
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
        // The finished step stays up a few seconds so it can be read.
        Assert.Equal("Leave the base", BrowserFrameBuilder.Capture(session, includeEvents: false).Guidance.Tutorial?.Title);
        for (int tick = 0; tick < 5 * Tweaks.Perf.TargetFps; tick++)
            session.Advance(default);
        var outside = BrowserFrameBuilder.Capture(session, includeEvents: false);
        Assert.Equal("Mine pyrite", outside.Guidance.Tutorial?.Title);
        Assert.False(outside.Guidance.ShowUpgrades);
        Assert.Contains("\"guidance\":{\"tutorial\":", JsonSerializer.Serialize(outside, BrowserJsonContext.Default.BrowserFrame),
            StringComparison.Ordinal);
    }
}

public class BrowserStressTests
{
    /// <summary>The busy world the browser build is measured in (?machines=N) is networks a player could have built.</summary>
    [Fact]
    [Trait("Category", "Slow")]
    public void Stress_GrowsPoweredNetworksFromEveryBase_WithHarvesters()
    {
        var session = new BrowserSession(1234, machines: 40);
        var machines = session.World.Machines.Machines;

        Assert.Equal(40, session.Machines);
        Assert.Equal(40, machines.Count);
        foreach (var tank in session.World.TankList.Tanks)
            Assert.Contains(machines, m => m.StandsFor(tank.Base!) && m.Type == MachineType.Relay && m.LinkPoint?.IsPowered == true);
        // A harvester and a second relay beside every third frontier relay, at most: the rest extend the networks.
        int relays = machines.Count(m => m.Type == MachineType.Relay);
        int harvesters = machines.Count(m => m.Type == MachineType.Harvester);
        Assert.InRange(harvesters, 1, relays / 3);
    }
}

public class BrowserMachineUpgradeInputTests
{
    /// <summary>
    /// The page queues clicks and keys and sends one a step. A purchase closes the machine upgrade panel, so a second
    /// choice already queued arrives with the panel shut: the mark it was queued with keeps it a machine purchase,
    /// never a base upgrade. A key queued with the panel shut is a base upgrade, as before.
    /// </summary>
    [Fact]
    public void AKeyQueuedForTheMachine_StaysAMachinePurchase_AfterThePanelCloses()
    {
        var session = new BrowserSession(1234);
        Assert.False(session.MachineUpgrades.IsOpen);

        var queued = session.WithMachineUpgradePanel(new ControllerOutput { Upgrade = 1 }, togglePanel: false, forMachine: true);
        Assert.True(queued.UpgradeMachine);

        var baseKey = session.WithMachineUpgradePanel(new ControllerOutput { Upgrade = 1 }, togglePanel: false);
        Assert.False(baseKey.UpgradeMachine);

        var noKey = session.WithMachineUpgradePanel(default, togglePanel: false, forMachine: true);
        Assert.False(noKey.UpgradeMachine);
    }

    /// <summary>
    /// The build buttons write the key that builds each machine, the desktop's label: the generator and the turret
    /// kept Y and U when the pylon's third key went, so the page must not number the keys itself.
    /// </summary>
    [Fact]
    public void BuildButtons_WriteTheDesktopsKeyLabels()
    {
        var session = new BrowserSession(1234);

        var labels = BrowserFrameBuilder.Capture(session, includeEvents: false).Builds
            .ToDictionary(b => b.Name, b => b.KeyLabel);

        Assert.Equal("E", labels["Relay"]);
        Assert.Equal("R", labels["Harvester"]);
        Assert.Equal("Y", labels["Generator"]);
        Assert.Equal("U", labels["Turret"]);
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
