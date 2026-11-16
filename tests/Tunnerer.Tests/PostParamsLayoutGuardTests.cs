using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Tunnerer.Desktop.Rendering;

namespace Tunnerer.Tests;

/// <summary>
/// <c>cbuffer PostParams</c> is described twice: in HLSL (PostPs/Common/PostParams.hlsli) and as the C#
/// <see cref="PostParamsBlock"/> uploaded into it. This test packs the HLSL declaration with the constant-buffer
/// rules (16-byte rows; a vector never straddles a row; every array element starts a row) and checks that each
/// C# field sits at the byte the shader reads it from.
/// </summary>
public partial class PostParamsLayoutGuardTests
{
    private const int RowBytes = 16;

    private static readonly string PostParamsPath = Path.Combine(EntityLayoutGuardTests.FindRepositoryRoot(),
        "src", "Tunnerer.Desktop", "Rendering", "Shaders", "PostPs", "Common", "PostParams.hlsli");

    /// <summary>
    /// HLSL member -> the C# field holding each of its components (null for a component no C# field fills).
    /// An array member maps to the C# fixed buffer, whose elements follow the HLSL elements one float4 each.
    /// </summary>
    private static readonly Dictionary<string, string?[]> CSharpFields = new(StringComparer.Ordinal)
    {
        ["TexelSize"] = ["TexelSizeX", "TexelSizeY"],
        ["PixelScale"] = ["PixelScale"],
        ["Time"] = ["Time"],
        ["WorldSize"] = ["WorldSizeX", "WorldSizeY"],
        ["CameraPixels"] = ["CameraPixelsX", "CameraPixelsY"],
        ["ViewSize"] = ["ViewSizeX", "ViewSizeY"],
        ["UseTerrainAux"] = ["UseTerrainAux"],
        ["PostParamsLayoutVersion"] = ["PostParamsLayoutVersion"],
        ["HeatDebugOverlay"] = ["HeatDebugOverlay"],
        ["PostVignetteEnabled"] = ["PostVignetteEnabled"],
        ["_padReserved0"] = ["_padReserved0"],
        ["PostTerrainAuxEnabled"] = ["PostTerrainAuxEnabled"],
        ["PostTankGlowEnabled"] = ["PostTankGlowEnabled"],
        ["PostTerrainHeatEnabled"] = ["PostTerrainHeatEnabled"],
        ["PostThermalRegionsEnabled"] = ["PostThermalRegionsEnabled"],
        ["ThermalTileSizeCells"] = ["ThermalTileSizeCells"],
        ["ThermalTileHeatThreshold01"] = ["ThermalTileHeatThreshold01"],
        ["_padReserved1"] = ["_padReserved1"],
        ["VignetteStrength"] = ["VignetteStrength"],
        ["VignetteInnerRadius"] = ["VignetteInnerRadius"],
        ["VignetteOuterRadius"] = ["VignetteOuterRadius"],
        ["_padReserved2"] = ["_padReserved2"],
        ["TerrainMaskCaveDarken"] = ["TerrainMaskCaveDarken"],
        ["TerrainMaskSolidLift"] = ["TerrainMaskSolidLift"],
        ["_padReserved3"] = ["_padReserved3"],
        ["TerrainMaskRimLift"] = ["TerrainMaskRimLift"],
        ["_padReserved4"] = ["_padReserved4"],
        ["Quality"] = ["Quality"],
        ["MaterialEmissiveGold"] = ["MaterialGoldR", "MaterialGoldG", "MaterialGoldB", "MaterialGoldStrength"],
        ["MaterialEmissiveScorched"] =
            ["MaterialScorchedR", "MaterialScorchedG", "MaterialScorchedB", "MaterialScorchedStrength"],
        ["MaterialEmissivePulse"] = ["MaterialPulseFreq", "MaterialPulseMin", "MaterialPulseRange", "MaterialPulsePad"],
        ["NativeContinuousParams"] =
            ["NativeSmoothingEnabled", "_padReserved5", "NativeSampleFactor", "NativeCurvingEnabled"],
        ["LightDir"] = ["LightDirX", "LightDirY", "LightDirZ", "LightNormalStrength"],
        ["HalfVector"] = ["HalfVecX", "HalfVecY", "HalfVecZ", "LightMicroNormalStrength"],
        ["LightParams"] = ["LightAmbient", "LightDiffuseWeight", "LightShininess", "LightSpecularIntensity"],
        ["TankHeatGlowColor"] = ["TankHeatGlowR", "TankHeatGlowG", "TankHeatGlowB", "TankHeatGlowA"],
        ["TerrainHeatGlowColorAndThreshold"] =
            ["TerrainHeatGlowR", "TerrainHeatGlowG", "TerrainHeatGlowB", "TerrainHeatThreshold"],
        ["ScorchTint"] = ["_padScorch0", "ScorchTintDarken", "ScorchTintStrength", "_padScorch1"],
        ["TankGlowCount"] = ["TankGlowCount"],
        ["TankGlow"] = ["TankGlow"],
    };

    [Fact]
    public void EveryHlslMember_MatchesItsPostParamsBlockFieldOffset()
    {
        var members = PackCbuffer(File.ReadAllText(PostParamsPath), "PostParams");
        Assert.Equal(CSharpFields.Keys.Order(StringComparer.Ordinal), members.Select(m => m.Name).Order(StringComparer.Ordinal));

        var mismatches = new List<string>();
        foreach (var member in members)
        {
            string?[] fields = CSharpFields[member.Name];
            if (member.ArrayLength > 0)
            {
                Assert.Single(fields);
                Assert.Equal(PostParamsBlock.MaxTankGlows, member.ArrayLength);
                Assert.Equal(4, member.Components); // a float4 array is contiguous, like the C# fixed buffer
                CheckOffset(fields[0]!, member.Offset, $"{member.Name}[0]", mismatches);
                continue;
            }

            Assert.True(fields.Length == member.Components,
                $"{member.Name} has {member.Components} component(s) in HLSL but {fields.Length} C# field(s) in the table.");
            for (int c = 0; c < fields.Length; c++)
            {
                if (fields[c] is { } field)
                    CheckOffset(field, member.Offset + c * sizeof(float), $"{member.Name}.{"xyzw"[c]}", mismatches);
            }
        }

        Assert.True(mismatches.Count == 0,
            "PostParamsBlock does not match cbuffer PostParams in PostParams.hlsli:\n" + string.Join('\n', mismatches));
    }

    [Fact]
    public void PostParamsBlock_HasNoUnmappedFieldsAndCoversTheCbuffer()
    {
        var members = PackCbuffer(File.ReadAllText(PostParamsPath), "PostParams");
        var mapped = CSharpFields.Values.SelectMany(f => f).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var fields = typeof(PostParamsBlock).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        // Fields that are not in the table may only be trailing row padding the HLSL leaves implicit.
        var unmapped = fields.Select(f => f.Name).Where(n => !mapped.Contains(n) && !n.StartsWith("_pad", StringComparison.Ordinal));
        Assert.Empty(unmapped);
        foreach (string name in mapped)
            Assert.Contains(fields, f => f.Name == name);

        var last = members[^1];
        int hlslSize = last.Offset + (last.ArrayLength > 0 ? (last.ArrayLength - 1) * RowBytes : 0) + last.Components * sizeof(float);
        Assert.Equal(AlignUp(hlslSize, RowBytes), Marshal.SizeOf<PostParamsBlock>());
    }

    [Fact]
    public void Packer_FollowsCbufferRules()
    {
        const string source = """
            cbuffer T : FRAGMENT_UNIFORMS(0)
            {
                float a;      // 0
                float3 b;     // 4: fits in row 0
                float2 c;     // 16
                float3 d;     // 32: would straddle row 1
                float e;      // 44
                float f[2];   // 48, then 64
                float g;      // 68: packs after the last array element
            };
            """;
        var members = PackCbuffer(source, "T");
        Assert.Equal([0, 4, 16, 32, 44, 48, 68], members.Select(m => m.Offset));
    }

    private static void CheckOffset(string field, int hlslOffset, string hlslName, List<string> mismatches)
    {
        int csharpOffset = (int)Marshal.OffsetOf<PostParamsBlock>(field);
        if (csharpOffset != hlslOffset)
            mismatches.Add($"  {hlslName} is at byte {hlslOffset} in HLSL, but PostParamsBlock.{field} is at byte {csharpOffset}.");
    }

    private sealed record Member(string Name, int Components, int ArrayLength, int Offset);

    /// <summary>Byte offsets of a cbuffer's members (float, float2..4 and arrays of them) under HLSL packing.</summary>
    private static List<Member> PackCbuffer(string source, string cbufferName)
    {
        string code = Regex.Replace(source, @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);
        var block = Regex.Match(code, @"\bcbuffer\s+" + cbufferName + @"\b[^{]*\{(?<body>[^}]*)\}");
        Assert.True(block.Success, $"cbuffer {cbufferName} not found");

        var members = new List<Member>();
        int offset = 0;
        foreach (string statement in block.Groups["body"].Value.Split(';'))
        {
            if (string.IsNullOrWhiteSpace(statement))
                continue;
            var m = MemberRegex().Match(statement);
            Assert.True(m.Success, $"Unsupported cbuffer member '{statement.Trim()}': teach PostParamsLayoutGuardTests its type.");
            int components = m.Groups["n"].Success ? int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
            int size = components * sizeof(float);
            if (m.Groups["len"].Success)
            {
                int length = int.Parse(m.Groups["len"].Value, System.Globalization.CultureInfo.InvariantCulture);
                offset = AlignUp(offset, RowBytes);
                members.Add(new Member(m.Groups["name"].Value, components, length, offset));
                offset += (length - 1) * RowBytes + size;
            }
            else
            {
                if (offset % RowBytes + size > RowBytes)
                    offset = AlignUp(offset, RowBytes);
                members.Add(new Member(m.Groups["name"].Value, components, 0, offset));
                offset += size;
            }
        }
        return members;
    }

    private static int AlignUp(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

    [GeneratedRegex(@"^\s*float(?<n>[1-4])?\s+(?<name>\w+)\s*(\[\s*(?<len>\d+)\s*\])?\s*$")]
    private static partial Regex MemberRegex();
}
