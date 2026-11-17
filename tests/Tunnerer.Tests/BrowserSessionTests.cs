namespace Tunnerer.Tests;

using System.Text.Json;
using Tunnerer.Browser.Client;
using Tunnerer.Core;
using Tunnerer.Core.Entities.Bases;
using Tunnerer.Core.Entities.Projectiles;
using Tunnerer.Core.Input;
using Tunnerer.Core.Types;
using System.Runtime.InteropServices;
using Tunnerer.Desktop.Rendering;
using Tunnerer.Desktop.Rendering.Entities;

public class BrowserSessionTests
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
        Assert.Empty(frame.Events);
        var desktopEntities = new EntityLayerBuilder();
        desktopEntities.CaptureStep(session.World, 0);
        desktopEntities.Build(session.Scene.View, 1, 0, false, 0, session.Vision, session.Radar);
        Assert.Equal(MemoryMarshal.AsBytes(desktopEntities.Vertices.AsSpan(0, desktopEntities.VertexCount)).ToArray(), session.Scene.Vertices);
        Assert.Equal(Marshal.SizeOf<PostParamsBlock>(), session.Scene.Uniforms.Length);

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
        Assert.Contains(built.Events, e => e.Kind == "MachineBuilt");

        Position start = player.Position;
        for (int i = 0; i < 30; i++)
            session.Advance(new ControllerOutput { MoveSpeed = new Offset(0, -1) });
        Assert.True(player.Position.Y < start.Y);
        session.Scene.Render(int.MaxValue, int.MaxValue, 1, 1, 0, 1);
        Assert.True(session.View.Width <= 640);
        Assert.True(session.View.Height <= 400);
        // A live projectile in visible open ground must become the same hot-shot quad used by desktop.
        session.World.Projectiles.Add(Projectile.CreateBullet(home.Position, new VectorF(1, 0), player.Color));
        session.Advance(default);
        session.Scene.Render(1280, 720, 6, 1, 0, 2);
        Assert.Contains(MemoryMarshal.Cast<byte, EntityVertex>(session.Scene.Vertices).ToArray(),
            v => v.Kind == EntityShape.KindHotShot);
    }
}
