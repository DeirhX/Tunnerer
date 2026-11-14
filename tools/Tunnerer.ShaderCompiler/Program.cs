using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tunnerer.ShaderCompiler;

// Usage: Tunnerer.ShaderCompiler --out <dir> <file.hlsl>=<vertex|fragment> [...]
//
// For each HLSL file writes <name>.spv (Vulkan), <name>.dxil (Direct3D 12), <name>.msl (Metal) and <name>.json (the
// resource counts and entry points SDL_CreateGPUShader needs). The HLSL entry point is always "main"; resources are
// declared in SDL_GPU's register spaces through the macros in PostPs/Common/Registers.hlsli.

string? outDir = null;
var shaders = new List<(string Path, ShaderCross.ShaderStage Stage)>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--out" && i + 1 < args.Length)
    {
        outDir = args[++i];
        continue;
    }

    int eq = args[i].LastIndexOf('=');
    if (eq <= 0)
        return Fail($"Expected <file.hlsl>=<vertex|fragment>, got '{args[i]}'.");
    string stageName = args[i][(eq + 1)..];
    ShaderCross.ShaderStage stage = stageName switch
    {
        "vertex" => ShaderCross.ShaderStage.Vertex,
        "fragment" => ShaderCross.ShaderStage.Fragment,
        _ => (ShaderCross.ShaderStage)(-1),
    };
    if ((int)stage < 0)
        return Fail($"Unknown shader stage '{stageName}' in '{args[i]}'.");
    shaders.Add((Path.GetFullPath(args[i][..eq]), stage));
}

if (outDir == null || shaders.Count == 0)
    return Fail("Usage: Tunnerer.ShaderCompiler --out <dir> <file.hlsl>=<vertex|fragment> [...]");

Directory.CreateDirectory(outDir);
ShaderCross.UseVersionedSdlLibrary();
if (!ShaderCross.Init())
    return Fail($"SDL_ShaderCross_Init failed: {ShaderCross.LastError()}");

try
{
    foreach (var (path, stage) in shaders)
    {
        if (!Compiler.CompileOne(path, stage, outDir, out string error))
            return Fail($"{path}: {error}");
    }
}
finally
{
    ShaderCross.Quit();
}

Console.WriteLine($"[Shaders] Compiled {shaders.Count} shader(s) to DXIL, SPIR-V and MSL in {outDir}");
return 0;

static int Fail(string message)
{
    // MSBuild shows "error:" lines as build errors.
    Console.Error.WriteLine($"error: {message}");
    return 1;
}

internal static unsafe partial class Compiler
{
    private const string Entrypoint = "main";

    public static bool CompileOne(string path, ShaderCross.ShaderStage stage, string outDir, out string error)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        string source = File.ReadAllText(path);
        string includeDir = Path.GetDirectoryName(path)!;

        byte[] spirv;
        byte[] dxil;
        using (var hlsl = new HlslInput(source, includeDir, stage))
        {
            ShaderCross.HlslInfo info = hlsl.Info;
            if (!Run(&info, &ShaderCross.CompileSpirvFromHlsl, out spirv))
            {
                error = $"HLSL to SPIR-V failed: {ShaderCross.LastError()}";
                return false;
            }
            if (!Run(&info, &ShaderCross.CompileDxilFromHlsl, out dxil))
            {
                error = $"HLSL to DXIL failed: {ShaderCross.LastError()}";
                return false;
            }
        }

        ShaderCross.GraphicsShaderResourceInfo resources;
        string msl;
        fixed (byte* pSpirv = spirv)
        {
            var metadata = ShaderCross.ReflectGraphicsSpirv(pSpirv, (nuint)spirv.Length, 0);
            if (metadata == null)
            {
                error = $"SPIR-V reflection failed: {ShaderCross.LastError()}";
                return false;
            }
            resources = metadata->ResourceInfo;
            ShaderCross.SdlFree(metadata);

            // The renderer binds sampled textures only. A Texture2D without a SamplerState at the same index is
            // reflected as a storage texture and would be bound wrongly at run time, so stop here instead.
            if (resources.NumStorageTextures != 0 || resources.NumStorageBuffers != 0)
            {
                error = $"uses {resources.NumStorageTextures} storage texture(s) and {resources.NumStorageBuffers} " +
                        "storage buffer(s). Give every Texture2D a SamplerState at the same index " +
                        "(FRAGMENT_TEXTURE(n) with FRAGMENT_SAMPLER(n)).";
                return false;
            }

            byte[] entry = Utf8(Entrypoint);
            fixed (byte* pEntry = entry)
            {
                var info = new ShaderCross.SpirvInfo
                {
                    Bytecode = pSpirv,
                    BytecodeSize = (nuint)spirv.Length,
                    Entrypoint = pEntry,
                    ShaderStage = stage,
                    Props = 0,
                };
                void* mslPtr = ShaderCross.TranspileMslFromSpirv(&info);
                if (mslPtr == null)
                {
                    error = $"SPIR-V to MSL failed: {ShaderCross.LastError()}";
                    return false;
                }
                msl = Marshal.PtrToStringUTF8((nint)mslPtr) ?? string.Empty;
                ShaderCross.SdlFree(mslPtr);
            }
        }

        // SPIRV-Cross renames "main" (reserved in MSL) to "main0"; read the name back rather than assume it.
        Match mslEntry = MslEntryPattern().Match(msl);
        if (!mslEntry.Success)
        {
            error = "Could not find the entry point in the generated MSL.";
            return false;
        }

        File.WriteAllBytes(Path.Combine(outDir, name + ".spv"), spirv);
        File.WriteAllBytes(Path.Combine(outDir, name + ".dxil"), dxil);
        File.WriteAllText(Path.Combine(outDir, name + ".msl"), msl);
        var json = new ShaderMetadataFile(
            Stage: stage == ShaderCross.ShaderStage.Vertex ? "vertex" : "fragment",
            Samplers: resources.NumSamplers,
            StorageTextures: resources.NumStorageTextures,
            StorageBuffers: resources.NumStorageBuffers,
            UniformBuffers: resources.NumUniformBuffers,
            SpirvEntrypoint: Entrypoint,
            DxilEntrypoint: Entrypoint,
            MslEntrypoint: mslEntry.Groups[1].Value);
        File.WriteAllText(Path.Combine(outDir, name + ".json"),
            JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));

        error = string.Empty;
        return true;
    }

    private static bool Run(ShaderCross.HlslInfo* info, delegate*<ShaderCross.HlslInfo*, nuint*, void*> compile, out byte[] output)
    {
        nuint size = 0;
        void* ptr = compile(info, &size);
        if (ptr == null || size == 0)
        {
            output = [];
            return false;
        }
        output = new byte[(int)size];
        Marshal.Copy((nint)ptr, output, 0, output.Length);
        ShaderCross.SdlFree(ptr);
        return true;
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + "\0");

    [GeneratedRegex(@"(?:vertex|fragment)\s+\w+\s+(\w+)\s*\(")]
    private static partial Regex MslEntryPattern();

    /// <summary>Holds the native strings for one HLSL compile so they stay alive across both compiles.</summary>
    private sealed class HlslInput : IDisposable
    {
        private readonly nint _source, _entry, _includeDir;
        public ShaderCross.HlslInfo Info;

        public HlslInput(string source, string includeDir, ShaderCross.ShaderStage stage)
        {
            _source = Marshal.StringToCoTaskMemUTF8(source);
            _entry = Marshal.StringToCoTaskMemUTF8(Entrypoint);
            _includeDir = Marshal.StringToCoTaskMemUTF8(includeDir);

            Info = new ShaderCross.HlslInfo
            {
                Source = (byte*)_source,
                Entrypoint = (byte*)_entry,
                IncludeDir = (byte*)_includeDir,
                Defines = null,
                ShaderStage = stage,
                Props = 0,
            };
        }

        public void Dispose()
        {
            Marshal.FreeCoTaskMem(_source);
            Marshal.FreeCoTaskMem(_entry);
            Marshal.FreeCoTaskMem(_includeDir);
        }
    }
}

internal sealed record ShaderMetadataFile(
    string Stage,
    uint Samplers,
    uint StorageTextures,
    uint StorageBuffers,
    uint UniformBuffers,
    string SpirvEntrypoint,
    string DxilEntrypoint,
    string MslEntrypoint);
