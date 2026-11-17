using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Input;
using Tunnerer.Core.Resources;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

namespace Tunnerer.Tests;

/// <summary>Mining emberite into the tank's hold, banking it at the base and spilling it on death (DESIGN.md §6.3).</summary>
public class OreMiningTests
{
    private static readonly ControllerOutput Right = new() { MoveSpeed = new Offset(1, 0) };
    private static readonly ControllerOutput TorchRight = new()
    {
        MoveSpeed = new Offset(1, 0),
        ShootPrimary = true,
        AimDirection = new DirectionF(1, 0),
    };

    private static void Fill(TerrainGrid terrain, Position center, int half, TerrainPixel pixel, int fromDx = int.MinValue)
    {
        for (int dy = -half; dy <= half; dy++)
            for (int dx = Math.Max(-half, fromDx); dx <= half; dx++)
                terrain.SetPixel(center + new Offset(dx, dy), pixel);
    }

    /// <summary>The tank in an open pocket outside its base, facing a wall of <paramref name="wall"/> to its right.</summary>
    private static RelayHeatTests.Scenario FacingWall(TerrainPixel wall)
    {
        var s = RelayHeatTests.CreateScenario();
        Fill(s.World.Terrain, s.RelayPos, 14, TerrainPixel.Blank);
        Fill(s.World.Terrain, s.RelayPos, 14, wall, fromDx: 5);
        s.Tank.Position = s.RelayPos;
        s.World.Advance(_ => default);
        return s;
    }

    private static int Drive(RelayHeatTests.Scenario s, ControllerOutput input, int ticks)
    {
        int collected = 0;
        for (int i = 0; i < ticks; i++)
        {
            s.World.Advance(t => t == 0 ? input : default);
            foreach (var e in s.World.StepEvents)
                if (e.Kind == StepEventKind.OreCollected && e.Color == s.Tank.Color)
                    collected += e.Count;
        }
        return collected;
    }

    private static int CountOre(TerrainGrid terrain, Position center, int half)
    {
        int units = 0;
        for (int dy = -half; dy <= half; dy++)
            for (int dx = -half; dx <= half; dx++)
                units += Pixel.OreUnits(terrain.GetPixelRaw(center + new Offset(dx, dy)));
        return units;
    }

    [Fact]
    public void PlainOre_YieldsToTheDig_SlowerThanDirt_AndFillsTheHold()
    {
        var ore = FacingWall(TerrainPixel.Emberite);
        var dirt = FacingWall(TerrainPixel.Dirt);
        int before = CountOre(ore.World.Terrain, ore.RelayPos, 14);

        int collected = Drive(ore, Right, 40);
        Drive(dirt, Right, 40);

        Assert.True(collected > 0, "the plain dig clears ore without the torch");
        Assert.Equal(collected, ore.Tank.OreHeld);
        Assert.Equal(before - collected, CountOre(ore.World.Terrain, ore.RelayPos, 14));
        Assert.True(dirt.Tank.Position.X > ore.Tank.Position.X + 3,
            $"ore digs slower than dirt. dirt x={dirt.Tank.Position.X}, ore x={ore.Tank.Position.X}");
    }

    [Fact]
    public void CoreCells_NeedTheTorch_AndAreWorthMore()
    {
        var s = FacingWall(TerrainPixel.EmberiteCore);
        Assert.Equal(0, Drive(s, Right, 60));
        Assert.Equal(0, s.Tank.OreHeld);

        int collected = Drive(s, TorchRight, 120);
        Assert.True(collected > 0, "the torch cuts core cells");
        Assert.Equal(0, collected % Tweaks.Ore.CoreCellUnits);
        Assert.Equal(collected, s.Tank.OreHeld);
    }

    [Fact]
    public void FullHold_LeavesOreInTheGround()
    {
        var s = FacingWall(TerrainPixel.Emberite);
        s.Tank.Resources.Current.Ore = Tweaks.Ore.TankHold - 2;
        int before = CountOre(s.World.Terrain, s.RelayPos, 14);

        Drive(s, Right, 120);

        Assert.True(s.Tank.IsOreHoldFull);
        Assert.Equal(Tweaks.Ore.TankHold, s.Tank.OreHeld);
        Assert.Equal(before - 2, CountOre(s.World.Terrain, s.RelayPos, 14));
    }

    [Fact]
    public void ACoreCell_IsLeftAlone_UnlessAllItsUnitsFit()
    {
        var s = FacingWall(TerrainPixel.EmberiteCore);
        s.Tank.Resources.Current.Ore = Tweaks.Ore.TankHold - (Tweaks.Ore.CoreCellUnits - 1);
        int before = CountOre(s.World.Terrain, s.RelayPos, 14);

        Assert.Equal(0, Drive(s, TorchRight, 150));
        Assert.Equal(before, CountOre(s.World.Terrain, s.RelayPos, 14));
        Assert.False(s.Tank.IsOreHoldFull, "two units of room are left, too few for a core cell");
    }

    [Fact]
    public void DockingAtHome_BanksTheHold()
    {
        var s = RelayHeatTests.CreateScenario();
        s.Tank.Position = s.Base.Position;
        s.Tank.Resources.Current.Ore = Tweaks.Ore.TankHold;
        int stored = s.Base.Materials.Ore;

        s.World.Advance(_ => default);
        Assert.Equal(Tweaks.Ore.TankHold - Tweaks.Base.HomeAbsorbOre, s.Tank.OreHeld);
        for (int i = 0; i < 10; i++)
            s.World.Advance(_ => default);

        Assert.Equal(0, s.Tank.OreHeld);
        Assert.Equal(stored + Tweaks.Ore.TankHold, s.Base.Materials.Ore);
    }

    [Fact]
    public void Death_SpillsTheHoldIntoTheGroundAroundTheWreck()
    {
        var s = FacingWall(TerrainPixel.Dirt);
        const int held = 25;
        s.Tank.Resources.Current.Ore = held;
        var at = s.Tank.Position;
        int before = CountOre(s.World.Terrain, at, Tweaks.Ore.DeathDropRadius + 1);

        s.Tank.Reactor.Exhaust(new ReactorState(0, s.Tank.Reactor.Health));
        s.World.Advance(_ => default);

        Assert.True(s.Tank.IsDead);
        Assert.Equal(0, s.Tank.OreHeld);
        Assert.Equal(before + held, CountOre(s.World.Terrain, at, Tweaks.Ore.DeathDropRadius + 1));
        Assert.True(Pixel.IsOre(s.World.Terrain.GetPixelRaw(at)), "the nearest cells fill first");
    }

    [Fact]
    public void Drop_FillsNearestCellsFirst_AndLosesWhatFindsNoRoom()
    {
        var t = new TerrainGrid(new Size(40, 40));
        for (int i = 0; i < t.Size.Area; i++)
            t.SetPixelRaw(i, TerrainPixel.Rock);
        var center = new Position(20, 20);
        // A plus of five open cells, then one more open cell just outside the drop radius.
        foreach (var o in new Offset[] { new(0, 0), new(1, 0), new(-1, 0), new(0, 1), new(0, -1) })
            t.SetPixelRaw(center + o, TerrainPixel.Blank);
        t.SetPixelRaw(center + new Offset(Tweaks.Ore.DeathDropRadius + 1, 0), TerrainPixel.Blank);

        Assert.Equal(1, OreDrop.Scatter(t, center, 1));
        Assert.Equal(TerrainPixel.Emberite, t.GetPixelRaw(center));
        Assert.Equal(4, OreDrop.Scatter(t, center, 30));
        Assert.Equal(TerrainPixel.Blank, t.GetPixelRaw(center + new Offset(Tweaks.Ore.DeathDropRadius + 1, 0)));
    }
}
