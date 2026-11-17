using Tunnerer.Core;

namespace Tunnerer.Tests;

public class ArchitectureGuardTests
{
    [Fact]
    public void CoreAssembly_DoesNotReferenceDesktopOrGraphicsHostPackages()
    {
        var references = typeof(World).Assembly
            .GetReferencedAssemblies()
            .Select(r => r.Name ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("Tunnerer.Desktop", references);
        Assert.DoesNotContain("ImGui.NET", references);
        Assert.DoesNotContain("SDL3-CS", references);
    }

    /// <summary>The relay server is built from the protocol alone, so a gameplay change never needs a new relay.</summary>
    [Fact]
    public void ProtocolAssembly_ReferencesNoGameProject()
    {
        var references = typeof(Tunnerer.Protocol.TickBundle).Assembly
            .GetReferencedAssemblies()
            .Select(r => r.Name ?? string.Empty);

        Assert.DoesNotContain(references, name => name.StartsWith("Tunnerer.", StringComparison.Ordinal));
    }

    [Fact]
    public void RelayAssembly_ReferencesOnlyTheProtocol()
    {
        var references = typeof(Tunnerer.Relay.MatchRoom).Assembly
            .GetReferencedAssemblies()
            .Select(r => r.Name ?? string.Empty)
            .Where(name => name.StartsWith("Tunnerer.", StringComparison.Ordinal));

        Assert.Equal(["Tunnerer.Protocol"], references);
    }

    [Fact]
    public void CoreAssembly_DoesNotContainLegacyGuiTypes()
    {
        bool hasLegacyGuiTypes = typeof(World).Assembly
            .GetTypes()
            .Any(t => t.Namespace is not null && t.Namespace.StartsWith("Tunnerer.Core.Gui", StringComparison.Ordinal));

        Assert.False(hasLegacyGuiTypes);
    }
}

[Collection(ConsoleTestGroup.Name)]
public class SimulationProfileTests
{
    [Fact]
    public void Report_WithoutSink_IsSilentAndResets()
    {
        var profile = new SimulationProfile
        {
            FrameCount = 100,
            Total = TimeSpan.FromMilliseconds(100),
        };

        var previousOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            profile.Report();
        }
        finally
        {
            Console.SetOut(previousOut);
        }

        Assert.Equal(string.Empty, writer.ToString());
        Assert.Equal(0, profile.FrameCount);
    }

    [Fact]
    public void Report_WithSink_EmitsAllProfileLines()
    {
        var lines = new List<string>();
        var profile = new SimulationProfile
        {
            ReportSink = lines.Add,
            FrameCount = 100,
            Total = TimeSpan.FromMilliseconds(100),
        };

        profile.Report();

        Assert.Equal(3, lines.Count);
        Assert.All(lines, line => Assert.StartsWith("[Profile", line, StringComparison.Ordinal));
    }

    [Fact]
    public void Report_WithEnergyAudit_AddsTheEnergyLine()
    {
        var lines = new List<string>();
        var profile = new SimulationProfile { ReportSink = lines.Add, AuditEnergy = true, FrameCount = 100 };

        profile.Report();

        Assert.Equal(4, lines.Count);
        Assert.StartsWith("[ProfileE]", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public void AWorldsSections_AreItsSystemsInStepOrder_AndOnlyThoseItHas()
    {
        var plain = TestHelpers.CreateSeededWorld(enableTerrainRegrowth: false);
        var withCore = TestHelpers.CreateSeededWorld(hotRockPocketCount: 4);

        Assert.Equal(
            ["coll", "heat", "proj", "tanks", "machines", "sprites", "bases", "match", "links", "dock", "vision"],
            plain.Profile.Sections);
        Assert.Equal(
            ["coll", "heat", "regrow", "proj", "tanks", "machines", "sprites", "bases", "goal", "match", "links", "dock", "vision"],
            withCore.Profile.Sections);
    }

    [Fact]
    public void AWorldsReport_TimesEverySystem()
    {
        var lines = new List<string>();
        var world = TestHelpers.CreateSeededWorld();
        world.Profile.ReportSink = lines.Add;

        TestSimulation.AdvanceIdle(world, frames: 100);

        string sections = lines.Single(line => line.StartsWith("[Profile] ", StringComparison.Ordinal));
        foreach (string label in world.Profile.Sections)
            Assert.Contains($" {label}=", sections, StringComparison.Ordinal);
    }
}
