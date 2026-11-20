using Emberward.Core.Config;
using Emberward.Core.Entities.Machines;
using Emberward.Core.Types;

namespace Emberward.Tests;

public class PlacementPreviewTests
{
    [Fact]
    public void Harvester_PreviewsTheHeatItWouldSense_AndNoLink()
    {
        var world = TestHelpers.CreateSeededWorld(new Size(320, 200));
        var spot = world.TankBases.GetSpawn(0)!.Position + new Offset(0, 40);
        float cold = world.PreviewPlacement(MachineType.Harvester, spot).SenseHeat;

        world.Terrain.AddHeatRadius(spot, 200, 6);
        var preview = world.PreviewPlacement(MachineType.Harvester, spot);

        Assert.Equal(default, preview.Link);
        Assert.True(preview.SenseHeat > cold, $"hot={preview.SenseHeat}, cold={cold}");
        float expected = world.Terrain.SampleAverageHeat(spot, Tweaks.Machine.HarvesterHeatSenseRadius) * 255f
            / Tweaks.Machine.HarvesterStallTemperature;
        Assert.Equal(expected, preview.SenseHeat);
    }

    [Fact]
    public void ARelay_PreviewsItsLink()
    {
        const MachineType type = MachineType.Relay;
        var world = TestHelpers.CreateSeededWorld(new Size(320, 200));
        var spot = world.TankBases.GetSpawn(0)!.Position + new Offset(0, 40);

        var preview = world.PreviewPlacement(type, spot);

        Assert.Equal(world.LinkMap.PreviewLink(spot, world.Terrain), preview.Link);
        Assert.Equal(0f, preview.SenseHeat);
    }
}
