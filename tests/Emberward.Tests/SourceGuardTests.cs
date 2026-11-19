using System.Text.RegularExpressions;

namespace Emberward.Tests;

/// <summary>
/// Source rules for Core and the repository, each a ratchet: today's offenders are listed by file and the list can
/// only shrink. A new offender fails, and so does a listed one that has been fixed, so its entry gets deleted.
/// </summary>
public class SourceGuardTests
{
    private const string CoreDir = "src/Emberward.Core";

    private static readonly (string Name, Regex Pattern)[] NonDeterministicApis =
    [
        ("Random.Shared", new Regex(@"\bRandom\.Shared\b")),
        ("new Random()", new Regex(@"\bnew\s+Random\(\s*\)")),
        ("Environment.TickCount", new Regex(@"\bEnvironment\.TickCount")),
        ("DateTime.Now", new Regex(@"\bDateTime\.Now\b")),
        ("DateTime.UtcNow", new Regex(@"\bDateTime\.UtcNow\b")),
        ("Stopwatch", new Regex(@"\bStopwatch\b")),
    ];

    // Profiling timers. They must never feed simulation state.
    private static readonly HashSet<(string File, string Pattern)> NonDeterministicAllowed =
    [
        ("src/Emberward.Core/Terrain/TerrainGrid.HeatTiles.cs", "Stopwatch"),
        ("src/Emberward.Core/World.cs", "Stopwatch"),
    ];

    // Thermal code reads its numbers through SimulationSettings so tests can inject their own.
    private static readonly HashSet<(string File, string Pattern)> ThermalTweaksAllowed =
    [
    ];

    // Mutable statics in Tweaks make results depend on what ran before (and on test order).
    private static readonly HashSet<(string File, string Pattern)> MutableTweaksAllowed =
    [
    ];

    // Math.Sin, Math.Pow and friends call the C runtime, which rounds differently on Windows and macOS/Linux.
    private static readonly Regex PlatformMath =
        new(@"\bMathF?\.(Sin|Cos|Tan|Asin|Acos|Atan|Atan2|Sinh|Cosh|Tanh|SinCos|Exp|Log|Log2|Log10|Pow|Cbrt|Hypot|FusedMultiplyAdd)\b");

    private const int MaxLines = 700;

    // Files already over the limit, capped at their size when the rule was added. Split them; never raise a cap.
    private static readonly Dictionary<string, int> LongFilesAllowed = new()
    {
    };

    [Fact]
    public void Core_DoesNotReadClocksOrUnseededRandomness()
    {
        var found = new HashSet<(string File, string Pattern)>();
        foreach (string path in RepositoryFiles.CSharpFiles(CoreDir))
        {
            string code = WithoutLineComments(File.ReadAllText(path));
            foreach (var (name, pattern) in NonDeterministicApis)
            {
                if (pattern.IsMatch(code))
                    found.Add((RepositoryFiles.Relative(path), name));
            }
        }

        AssertMatchesAllowList(found, NonDeterministicAllowed,
            "Simulation code must not read the clock or unseeded randomness (it breaks determinism and replays)");
    }

    [Fact]
    public void Core_UsesDeterministicMath()
    {
        var found = RepositoryFiles.CSharpFiles(CoreDir)
            .Where(path => Path.GetFileName(path) != "DeterministicMath.cs")
            .Select(path => (Path: path, Match: PlatformMath.Match(WithoutLineComments(File.ReadAllText(path)))))
            .Where(f => f.Match.Success)
            .Select(f => $"{RepositoryFiles.Relative(f.Path)} calls {f.Match.Value}")
            .ToList();

        Assert.True(found.Count == 0,
            "Core must call DeterministicMath, not Math/MathF transcendentals (they differ between operating systems)."
            + Environment.NewLine + string.Join(Environment.NewLine, found));
    }

    [Fact]
    public void ThermalCode_ReadsSettingsNotTweaks()
    {
        var tweaks = new Regex(@"\bTweaks\.");
        var files = RepositoryFiles.CSharpFiles(Path.Combine(CoreDir, "Thermal"))
            .Concat(RepositoryFiles.CSharpFiles(Path.Combine(CoreDir, "Terrain"))
                .Where(path => Path.GetFileName(path).StartsWith("TerrainGrid.Heat", StringComparison.Ordinal)))
            .Append(Path.Combine(RepositoryFiles.Root, CoreDir, "Entities", "Tanks", "TankThermals.cs"));

        var found = files
            .Where(path => tweaks.IsMatch(WithoutLineComments(File.ReadAllText(path))))
            .Select(path => (RepositoryFiles.Relative(path), "Tweaks."))
            .ToHashSet();

        AssertMatchesAllowList(found, ThermalTweaksAllowed,
            "Thermal code must read constants through SimulationSettings, not Tweaks");
    }

    /// <summary>
    /// During play a pixel changes through <c>SetPixel</c>, which books the heat and lists the cell as changed. Fog of
    /// war and the renderer follow that list instead of rereading the map, so a cell written behind its back would
    /// stay as it was on screen. Level generation and the grid itself write raw.
    /// </summary>
    [Fact]
    public void Core_ChangesPixelsThroughSetPixel_OutsideLevelGeneration()
    {
        (string Name, Regex Pattern)[] rawWrites =
        [
            ("SetPixelRaw", new Regex(@"\bSetPixelRaw\s*\(")),
            ("terrain[...] =", new Regex(@"\b\w*([tT]errain|[gG]rid)\w*\[[^\]\n]+\]\s*=[^=]")),
        ];
        string[] writesRaw =
        [
            "src/Emberward.Core/LevelGen/",
            "src/Emberward.Core/Terrain/TerrainGrid.",
            "src/Emberward.Core/Terrain/ITerrainPixelWriter.cs",
        ];
        var found = new HashSet<(string File, string Pattern)>();
        foreach (string path in RepositoryFiles.CSharpFiles(CoreDir))
        {
            string file = RepositoryFiles.Relative(path);
            if (writesRaw.Any(prefix => file.StartsWith(prefix, StringComparison.Ordinal)))
                continue;
            string code = WithoutLineComments(File.ReadAllText(path));
            foreach (var (name, pattern) in rawWrites)
                if (pattern.IsMatch(code))
                    found.Add((file, name));
        }

        // World.Initialize copies the generated level into its grid, before anything can follow the terrain.
        AssertMatchesAllowList(found, [("src/Emberward.Core/World.cs", "terrain[...] =")],
            "Simulation code must change pixels through SetPixel, so that vision and the renderer learn of it");
    }

    [Fact]
    public void Tweaks_HasNoMutableStatics()
    {
        const string file = CoreDir + "/Config/Tweaks.cs";
        // A static auto-property with a setter, or a static field that is neither readonly nor const.
        var settable = new Regex(@"\bstatic\s+[\w<>\[\],.?\s]+?\s+(?<name>\w+)\s*\{\s*get;\s*(set|init);");
        var field = new Regex(@"^\s*(?:(?:public|internal|private|protected)\s+)*static\s+(?!readonly\b|class\b|partial\b|const\b)[\w<>\[\],.?]+\s+(?<name>\w+)\s*(?:=(?!>)|;)",
            RegexOptions.Multiline);

        string code = WithoutLineComments(File.ReadAllText(Path.Combine(RepositoryFiles.Root, file)));
        var found = settable.Matches(code).Concat(field.Matches(code))
            .Select(m => (file, m.Groups["name"].Value))
            .ToHashSet();

        AssertMatchesAllowList(found, MutableTweaksAllowed,
            "Tweaks must hold constants only (const, static readonly or get-only)");
    }

    [Fact]
    public void SourceFiles_StayUnderTheLineLimit()
    {
        var problems = new List<string>();
        var seenLong = new HashSet<string>();
        foreach (string path in RepositoryFiles.CSharpFiles("src").Concat(RepositoryFiles.CSharpFiles("tests")))
        {
            string file = RepositoryFiles.Relative(path);
            int lines = File.ReadAllLines(path).Length;
            if (lines <= MaxLines)
                continue;

            seenLong.Add(file);
            if (!LongFilesAllowed.TryGetValue(file, out int cap))
                problems.Add($"{file} has {lines} lines (limit {MaxLines}); split it.");
            else if (lines > cap)
                problems.Add($"{file} has {lines} lines, above its cap of {cap}; split it instead of growing it.");
        }

        foreach (string file in LongFilesAllowed.Keys.Where(file => !seenLong.Contains(file)))
            problems.Add($"{file} is no longer over {MaxLines} lines (or is gone); delete its entry from LongFilesAllowed.");

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static void AssertMatchesAllowList(
        HashSet<(string File, string Pattern)> found, HashSet<(string File, string Pattern)> allowed, string rule)
    {
        var problems = found.Except(allowed).Order()
            .Select(o => $"New offender: {o.File} uses '{o.Pattern}'.")
            .Concat(allowed.Except(found).Order()
                .Select(o => $"Fixed: {o.File} no longer uses '{o.Pattern}'; delete its allow-list entry."))
            .ToList();

        Assert.True(problems.Count == 0, rule + "." + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    private static string WithoutLineComments(string code) => Regex.Replace(code, @"//.*$", "", RegexOptions.Multiline);
}
