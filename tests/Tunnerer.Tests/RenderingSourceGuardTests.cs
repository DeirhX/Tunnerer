using System.Text.RegularExpressions;

namespace Tunnerer.Tests;

/// <summary>
/// Source rules that keep SDL_GPU calls inside their backend and the HLSL declaring resources the way SDL_GPU needs. The shader build step
/// (tools/Tunnerer.ShaderCompiler) checks what these tests cannot: that every shader compiles for DXIL, SPIR-V and
/// MSL and uses no storage resources.
/// </summary>
public class RenderingSourceGuardTests
{
    private static readonly string DesktopDir = Path.Combine(RepositoryFiles.Root, "src", "Tunnerer.Desktop");
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
