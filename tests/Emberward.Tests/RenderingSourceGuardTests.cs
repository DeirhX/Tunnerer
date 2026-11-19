using System.Text.RegularExpressions;

namespace Emberward.Tests;

/// <summary>
/// Source rules that keep SDL_GPU calls inside their backend and the HLSL declaring resources the way SDL_GPU needs. The shader build step
/// (tools/Emberward.ShaderCompiler) checks what these tests cannot: that every shader compiles for DXIL, SPIR-V and
/// MSL and uses no storage resources.
/// </summary>
public class RenderingSourceGuardTests
{
    private static readonly string DesktopDir = Path.Combine(RepositoryFiles.Root, "src", "Emberward.Desktop");
    private static readonly string ShaderDir = Path.Combine(DesktopDir, "Rendering", "Shaders");

    [Fact]
    public void Shaders_DeclareResourcesOnlyThroughRegisterMacros()
    {
        // SDL_GPU requires register spaces, so the space is chosen in one place: PostPs/Common/Registers.hlsli.
        var offenders = Directory.EnumerateFiles(ShaderDir, "*.hlsl*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "Registers.hlsli")
            .Where(path => File.ReadAllText(path).Contains("register(", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(ShaderDir, path))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Use the FRAGMENT_TEXTURE/FRAGMENT_SAMPLER/FRAGMENT_UNIFORMS/VERTEX_UNIFORMS macros instead of register() in: " +
            string.Join(", ", offenders));
    }

    // Functions that return before their end today. Each is small or desktop-only; remove an entry once its function
    // has one return at the end.
    private static readonly string[] EarlyReturnAllowList =
    [
        "Entity/Beams.hlsli:BeamTransition",
        "Entity/Beams.hlsli:LinkBeam",
        "Entity/Overlays.hlsli:RadarBlip",
        "HeatOrbPs.hlsl:BubbleLayer",
        "HeatOrbPs.hlsl:main",
        "PostPs/Common/TerrainEdge.hlsli:GrowFreshnessAtCell",
        "PostPs/Passes/TankGlowPass.hlsli:ApplyTankHeatGlowPass",
        "TerrainPs.hlsl:main",
    ];

    private static readonly Regex ShaderFunction = new(
        @"^[A-Za-z_]\w*(?:\s+[A-Za-z_]\w*)?\s+([A-Za-z_]\w*)\s*\([^;{}]*\)\s*(?::\s*\w+\s*)?\{",
        RegexOptions.Multiline);

    [Fact]
    public void Shaders_ReturnOnlyAtTheEndOfAFunction()
    {
        // DXC turns a function with a return before its end into a loop that runs once, left by a break at each
        // return. Windows browsers compile WebGL through ANGLE's Direct3D 11 backend and FXC, which took minutes over
        // that form in PostPs and EntityPs, or crashed, while the page waited (docs/browser.md). Write
        // "if (needed) { ... }" instead of "if (!needed) return;": the GPU skips the same work.
        var found = new List<string>();
        foreach (string path in Directory.EnumerateFiles(ShaderDir, "*.hlsl*", SearchOption.AllDirectories))
        {
            string source = Regex.Replace(File.ReadAllText(path), @"/\*.*?\*/|//[^\n]*", "", RegexOptions.Singleline);
            string file = Path.GetRelativePath(ShaderDir, path).Replace(Path.DirectorySeparatorChar, '/');
            foreach (Match function in ShaderFunction.Matches(source))
            {
                string body = FunctionBody(source, function.Index + function.Length - 1);
                bool early = Regex.Matches(body, @"\breturn\b[^;]*;")
                    .Any(r => body[(r.Index + r.Length)..].Any(c => !char.IsWhiteSpace(c) && c != '}'));
                if (early)
                    found.Add($"{file}:{function.Groups[1].Value}");
            }
        }

        var added = found.Except(EarlyReturnAllowList).ToList();
        var fixedOnes = EarlyReturnAllowList.Except(found).ToList();
        Assert.True(added.Count == 0,
            "Return only at the end of a shader function (wrap the rest in an if instead): " + string.Join(", ", added));
        Assert.True(fixedOnes.Count == 0,
            "These no longer return early; remove them from EarlyReturnAllowList: " + string.Join(", ", fixedOnes));
    }

    // The text between the brace at `open` and the brace that closes it.
    private static string FunctionBody(string source, int open)
    {
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[(open + 1)..i];
        }
        throw new InvalidOperationException("Unbalanced braces in a shader.");
    }

    [Fact]
    public void SdlGpu_IsUsedOnlyByTheSdlGpuBackend()
    {
        AssertOnlyUnder(new Regex(@"\bSDL\.\w*GPU\w*\("), Path.Combine("Rendering", "SdlGpu"));
    }

    private static void AssertOnlyUnder(Regex pattern, string allowedRelativeDir)
    {
        string allowed = Path.Combine(DesktopDir, allowedRelativeDir) + Path.DirectorySeparatorChar;
        var offenders = Directory.EnumerateFiles(DesktopDir, "*.cs", SearchOption.AllDirectories)
            .Where(path => !RepositoryFiles.IsBuildOutput(path))
            .Where(path => !path.StartsWith(allowed, StringComparison.Ordinal))
            .Where(path => pattern.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(DesktopDir, path))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"'{pattern}' belongs in {allowedRelativeDir} only, but appears in: {string.Join(", ", offenders)}");
    }
}
