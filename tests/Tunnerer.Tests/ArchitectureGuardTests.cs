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

    [Fact]
    public void CoreAssembly_DoesNotContainLegacyGuiTypes()
    {
        bool hasLegacyGuiTypes = typeof(World).Assembly
            .GetTypes()
            .Any(t => t.Namespace is not null && t.Namespace.StartsWith("Tunnerer.Core.Gui", StringComparison.Ordinal));

        Assert.False(hasLegacyGuiTypes);
    }
}

public class SimulationProfileTests
{
    [Fact]
    public void Report_WithoutSink_IsSilentAndResets()
    {
        var profile = new SimulationProfile
        {
            FrameCount = 100,
            Total = TimeSpan.FromMilliseconds(100),
            Collision = TimeSpan.FromMilliseconds(10),
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
            Collision = TimeSpan.FromMilliseconds(10),
        };

        profile.Report();

        Assert.Equal(4, lines.Count);
        Assert.All(lines, line => Assert.StartsWith("[Profile", line, StringComparison.Ordinal));
    }
}
