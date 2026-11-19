using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SDL3;
using Emberward.Core.Sensing;
using Emberward.Desktop.Rendering;
using Emberward.Desktop.Rendering.Entities;
using Emberward.Desktop.Rendering.SdlGpu;

namespace Emberward.Tests;

/// <summary>
/// The entity layer is described twice, once in C# and once in HLSL. These tests fail when the two drift apart:
/// the vertex struct against the pipeline's attribute table, and the shape kinds and machine flags against
/// the entity shader (constants in Entity/Layout.hlsli, dispatch in EntityPs.hlsl).
/// </summary>
public class EntityLayoutGuardTests
{
    private static readonly string ShaderDir = Path.Combine(FindRepositoryRoot(), "src", "Emberward.Desktop", "Rendering", "Shaders");
    private static readonly string EntityPsPath = Path.Combine(ShaderDir, "EntityPs.hlsl");
    private static readonly string LayoutPath = Path.Combine(ShaderDir, "Entity", "Layout.hlsli");
    private static readonly string OverlaysPath = Path.Combine(ShaderDir, "Entity", "Overlays.hlsli");

    [Fact]
    public void EntityVertex_MatchesVertexAttributeTable()
    {
        // Attribute location -> the first field of EntityVertex it reads, and how many floats it spans.
        (string Field, int Floats)[] expected =
        [
            (nameof(EntityVertex.ClipX), 2),
            (nameof(EntityVertex.LocalX), 2),
            (nameof(EntityVertex.R), 4),
            (nameof(EntityVertex.P0), 4),
        ];

        var attributes = SdlGpuBackend.EntityVertexAttributes;
        Assert.Equal(expected.Length, attributes.Length);

        int end = 0;
        for (int i = 0; i < attributes.Length; i++)
        {
            Assert.Equal((uint)i, attributes[i].Location);
            Assert.Equal(0u, attributes[i].BufferSlot);
            int fieldOffset = (int)Marshal.OffsetOf<EntityVertex>(expected[i].Field);
            Assert.Equal(fieldOffset, (int)attributes[i].Offset);
            Assert.Equal(expected[i].Floats * sizeof(float), FormatSize(attributes[i].Format));
            Assert.Equal(end, fieldOffset);
            end = fieldOffset + FormatSize(attributes[i].Format);
        }

        Assert.Equal(Marshal.SizeOf<EntityVertex>(), end);
        Assert.Equal(Unsafe.SizeOf<EntityVertex>(), end);
    }

    [Fact]
    public void ShapeKinds_MatchEntityPs()
    {
        AssertConstantsMatch("Kind", "kKind");
    }

    [Fact]
    public void MachineFlags_MatchEntityPs()
    {
        AssertConstantsMatch("MachineFlag", "kMachineFlag");
    }

    [Fact]
    public void EntityPs_DispatchesEveryShapeKind()
    {
        string shader = File.ReadAllText(EntityPsPath);
        Assert.Contains("#include \"Entity/Layout.hlsli\"", shader, StringComparison.Ordinal);
        int main = shader.IndexOf("float4 main(", StringComparison.Ordinal);
        Assert.True(main >= 0, "EntityPs.hlsl has no main()");
        // Comments are stripped so a kind named only in a comment does not count as dispatched.
        string body = Regex.Replace(shader[main..], @"//[^\n]*", "");
        var kinds = ReadShaderConstants(File.ReadAllText(LayoutPath), "kKind");
        var compared = Regex.Matches(body, @"\bkind\s*<\s*kKind(?<name>\w+)\b")
            .Select(m => m.Groups["name"].Value)
            .ToHashSet();
        // Every kind has a `kind < kKindX + 0.5` branch except the highest, which is the final fallthrough.
        string fallthrough = kinds.MaxBy(kv => kv.Value).Key;
        var missing = kinds.Keys.Where(name => name != fallthrough && !compared.Contains(name)).ToList();
        Assert.True(missing.Count == 0, "EntityPs main() never dispatches: " + string.Join(", ", missing));
        Assert.DoesNotContain(fallthrough, compared);
    }

    [Fact]
    public void RadarEchoes_MatchTheBlipShader()
    {
        // Layout.hlsli lists the echo numbers the radar blip reads, in RadarEcho's order.
        var listed = Regex.Match(File.ReadAllText(LayoutPath), @"echo \(RadarEcho:(?<list>[^)]*)\)");
        Assert.True(listed.Success, "Entity/Layout.hlsli should list the radar echoes as `echo (RadarEcho: 0 solid, ...)`");
        string list = Regex.Replace(listed.Groups["list"].Value, @"\s*//\s*", " ");
        var shader = Regex.Matches(list, @"(?<value>\d+)\s+(?<name>\w+)")
            .Select(m => (int.Parse(m.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture),
                m.Groups["name"].Value.ToUpperInvariant()));
        var csharp = Enum.GetValues<RadarEcho>().Select(e => ((int)e, e.ToString().ToUpperInvariant()));
        Assert.Equal(csharp, shader);

        // RadarBlip tells every echo from the one below it: a comparison of `echo` with a threshold between them.
        string overlays = Regex.Replace(File.ReadAllText(OverlaysPath), @"//[^\n]*", "");
        int blip = overlays.IndexOf("float4 RadarBlip(", StringComparison.Ordinal);
        Assert.True(blip >= 0, "Overlays.hlsli has no RadarBlip()");
        var branches = Regex.Matches(overlays[blip..], @"\becho\s*[<>]\s*(?<at>[0-9.]+)")
            .Select(m => (int)Math.Ceiling(float.Parse(m.Groups["at"].Value, System.Globalization.CultureInfo.InvariantCulture)))
            .ToHashSet();
        var missing = Enum.GetValues<RadarEcho>().Where(e => (int)e > 0 && !branches.Contains((int)e)).ToList();
        Assert.True(missing.Count == 0, "RadarBlip in Overlays.hlsli has no branch for: " + string.Join(", ", missing));
    }

    private static void AssertConstantsMatch(string csharpPrefix, string shaderPrefix)
    {
        var csharp = typeof(EntityShape)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(float) && f.Name.StartsWith(csharpPrefix, StringComparison.Ordinal))
            .ToDictionary(f => f.Name[csharpPrefix.Length..], f => (float)f.GetRawConstantValue()!);
        var shader = ReadShaderConstants(File.ReadAllText(LayoutPath), shaderPrefix);

        Assert.NotEmpty(csharp);
        Assert.Equal(csharp.Keys.Order(), shader.Keys.Order());
        foreach (var (name, value) in csharp)
            Assert.True(value == shader[name], $"{csharpPrefix}{name} is {value} in C# but {shader[name]} in Entity/Layout.hlsli");
    }

    private static Dictionary<string, float> ReadShaderConstants(string shader, string prefix) =>
        Regex.Matches(shader, $@"static\s+const\s+float\s+{prefix}(\w+)\s*=\s*([0-9.]+)\s*;")
            .ToDictionary(m => m.Groups[1].Value,
                m => float.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));

    private static int FormatSize(SDL.GPUVertexElementFormat format) => format switch
    {
        SDL.GPUVertexElementFormat.Float => 4,
        SDL.GPUVertexElementFormat.Float2 => 8,
        SDL.GPUVertexElementFormat.Float3 => 12,
        SDL.GPUVertexElementFormat.Float4 => 16,
        _ => throw new NotSupportedException($"Add the size of {format} to the guard test."),
    };

    internal static string FindRepositoryRoot([CallerFilePath] string thisFile = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Emberward.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException($"Could not find Emberward.slnx above {thisFile}.");
    }
}
