using Tunnerer.Core;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Machines;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Gui.Guidance;

namespace Tunnerer.Tests;

/// <summary>The tutorial's next-step logic and the game-event toasts.</summary>
public class GuidanceTests
{
    private static Position OutsideTheDoor(RelayHeatTests.Scenario s) =>
        s.Base.Position + new Offset(0, s.DirY * (Tweaks.Base.BaseSize / 2 + 8));

    private static void Advance(RelayHeatTests.Scenario s, int ticks)
    {
        for (int i = 0; i < ticks; i++)
            s.World.Advance(_ => default);
    }

    [Fact]
    public void Tutorial_StartsByLeavingTheBase_PointsAtTheDoor_ThenAsksForDirt()
    {
        var s = RelayHeatTests.CreateScenario();
        var tutorial = new TutorialDirector();

        var first = tutorial.Update(s.World, s.Tank, 0f);
        Assert.NotNull(first);
        Assert.Equal(1, first.Value.Number);
        Assert.Equal("Leave the base", first.Value.Title);
        Assert.NotNull(first.Value.Target);

        s.Tank.Position = OutsideTheDoor(s);
        var next = tutorial.Update(s.World, s.Tank, 1f);
        Assert.Equal("Mine emberite", next!.Value.Title);
        Assert.Contains("/ " + Tweaks.Ore.RelayCost, next.Value.Progress);
    }

    [Fact]
    public void Tutorial_SkipsStepsThePlayerHasAlreadyDone()
    {
        var s = RelayHeatTests.CreateScenario();
        RelayHeatTests.PlantRelay(s);
        Advance(s, 12);   // the link map powers it
        s.Tank.Position = OutsideTheDoor(s);
        var tutorial = new TutorialDirector();

        var prompt = tutorial.Update(s.World, s.Tank, 0f);

        Assert.Equal("Stay on your network", prompt!.Value.Title);
    }

    [Fact]
    public void Tutorial_ExplainerSteps_FinishAfterTheyHaveBeenRead()
    {
        var s = RelayHeatTests.CreateScenario();
        RelayHeatTests.PlantRelay(s);
        Advance(s, 12);
        s.Tank.Position = OutsideTheDoor(s);
        var tutorial = new TutorialDirector();

        Assert.Equal("Stay on your network", tutorial.Update(s.World, s.Tank, 0f)!.Value.Title);
        Assert.Equal("Stay on your network", tutorial.Update(s.World, s.Tank, 5f)!.Value.Title);
        Assert.NotEqual("Stay on your network", tutorial.Update(s.World, s.Tank, 30f)!.Value.Title);
    }

    [Fact]
    public void Tutorial_CanBeSkipped()
    {
        var s = RelayHeatTests.CreateScenario();
        var tutorial = new TutorialDirector();
        tutorial.Skip();
        Assert.Null(tutorial.Update(s.World, s.Tank, 0f));
        Assert.True(tutorial.IsComplete);
    }

    [Fact]
    public void Events_ToastWhenARelayLinksAndAHarvesterStarts()
    {
        var s = RelayHeatTests.CreateScenario();
        var feed = new NotificationFeed();
        var events = new GameEventWatcher();
        events.Update(s.World, s.Tank, feed, 0f);
        Assert.Empty(feed.Active);

        RelayHeatTests.PlantRelay(s);
        Advance(s, 12);
        events.Update(s.World, s.Tank, feed, 1f);
        Assert.Contains(feed.Active, n => n.Key == "relay-linked");

        var harvester = new Machine(s.RelayPos + new Offset(0, s.DirY * 6), MachineType.Harvester, s.Tank.Color);
        s.World.Machines.Add(harvester);
        s.World.PlantMachine(harvester, s.Base);
        events.Update(s.World, s.Tank, feed, 2f);
        Assert.Contains(feed.Active, n => n.Key == "harvester-planted");
    }

    [Fact]
    public void Events_ReportWhatHarvestersBanked_NotWhatTheTankUnloaded_WhenAFrameRunsSeveralSteps()
    {
        var s = RelayHeatTests.CreateScenario();
        HarvesterHeatTests.SeedOre(s, depth: 6, length: 9);
        RelayHeatTests.PlantRelay(s, s.RelayPos + new Offset(0, s.DirY * -15));
        var harvester = new Machine(s.RelayPos, MachineType.Harvester, s.Tank.Color);
        s.World.Machines.Add(harvester);
        s.World.PlantMachine(harvester, s.Base);
        Advance(s, 12);
        // The tank stands in the same coverage, unloading its hold through the network while the harvester works.
        s.Tank.Position = s.RelayPos + new Offset(0, s.DirY * -8);
        s.Tank.Hold.Ore = Tweaks.Ore.TankHold;
        var feed = new NotificationFeed();
        var events = new GameEventWatcher();
        float now = 0f;
        events.Update(s.World, s.Tank, feed, now);

        const int stepsPerFrame = 4;
        int harvested = 0, unloaded = 0;
        Notification? report = null;
        for (int frame = 0; frame < 400 && report == null; frame++)
        {
            for (int i = 0; i < stepsPerFrame; i++)
            {
                s.World.Advance(_ => default);
                events.AfterStep(s.World, s.Tank);
                foreach (var e in s.World.StepEvents)
                {
                    if (e.Kind == StepEventKind.OreHarvested) harvested += e.Count;
                    if (e.Kind == StepEventKind.OreUnloaded) unloaded += e.Count;
                }
            }
            now += stepsPerFrame / (float)Tweaks.Perf.TargetFps;
            events.Update(s.World, s.Tank, feed, now);
            report = feed.Active.FirstOrDefault(n => n.Key == "harvested");
        }

        Assert.NotNull(report);
        Assert.True(harvested > 0 && unloaded > 0, $"both should have reached the store. harvested={harvested}, unloaded={unloaded}");
        Assert.Equal($"Harvesters delivered +{harvested} ore", report.Title);
    }

    [Fact]
    public void Feed_DoesNotRepeatAKeyWithinItsCooldown_AndDropsExpiredToasts()
    {
        var feed = new NotificationFeed();
        Assert.True(feed.Post(0f, "k", "A", "a", NotificationKind.Info, duration: 5f, cooldown: 10f));
        Assert.False(feed.Post(3f, "k", "A", "a", NotificationKind.Info, duration: 5f, cooldown: 10f));
        Assert.Single(feed.Active);

        feed.Update(6f);
        Assert.Empty(feed.Active);
        Assert.True(feed.Post(11f, "k", "A", "a", NotificationKind.Info, duration: 5f, cooldown: 10f));
    }

    [Fact]
    public void Feed_KeepsOnlyTheNewestFew()
    {
        var feed = new NotificationFeed();
        for (int i = 0; i < NotificationFeed.MaxVisible + 3; i++)
            feed.Post(i, $"k{i}", "T", "d", NotificationKind.Info);
        Assert.Equal(NotificationFeed.MaxVisible, feed.Active.Count);
        Assert.Equal($"k{NotificationFeed.MaxVisible + 2}", feed.Active[^1].Key);
    }
}
