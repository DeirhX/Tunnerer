using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SDL3;
using Tunnerer.Desktop.Rendering;
using Tunnerer.Desktop.Rendering.SdlGpu;

namespace Tunnerer.Tests;

/// <summary>
/// The entity layer is described twice, once in C# and once in HLSL. These tests fail when the two drift apart:
/// the vertex struct against the pipeline's attribute table, and the shape kinds and machine flags against
/// EntityPs.hlsl.
/// </summary>
public class EntityLayoutGuardTests
{
    private static readonly string EntityPsPath = Path.Combine(
        FindRepositoryRoot(), "src", "Tunnerer.Desktop", "Rendering", "Shaders", "EntityPs.hlsl");

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
        int main = shader.IndexOf("float4 main(", StringComparison.Ordinal);
        Assert.True(main >= 0, "EntityPs.hlsl has no main()");
        string body = shader[main..];
        var missing = ReadShaderConstants(shader, "kKind").Keys
            .Where(name => !Regex.IsMatch(body, $@"\bkKind{name}\b"))
            .ToList();
        Assert.True(missing.Count == 0, "EntityPs main() never dispatches: " + string.Join(", ", missing));
    }

    private static void AssertConstantsMatch(string csharpPrefix, string shaderPrefix)
    {
        var csharp = typeof(EntityLayerBuilder)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(float) && f.Name.StartsWith(csharpPrefix, StringComparison.Ordinal))
            .ToDictionary(f => f.Name[csharpPrefix.Length..], f => (float)f.GetRawConstantValue()!);
        var shader = ReadShaderConstants(File.ReadAllText(EntityPsPath), shaderPrefix);

        Assert.NotEmpty(csharp);
        Assert.Equal(csharp.Keys.Order(), shader.Keys.Order());
        foreach (var (name, value) in csharp)
            Assert.True(value == shader[name], $"{csharpPrefix}{name} is {value} in C# but {shader[name]} in EntityPs.hlsl");
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

    private static string FindRepositoryRoot([CallerFilePath] string thisFile = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Tunnerer.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException($"Could not find Tunnerer.slnx above {thisFile}.");
    }
}
