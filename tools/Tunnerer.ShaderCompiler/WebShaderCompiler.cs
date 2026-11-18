namespace Tunnerer.ShaderCompiler;

using System.Runtime.InteropServices;

/// <summary>Compiles the same SPIR-V as desktop into WebGL 2's GLSL ES 3.00, using the bundled SPIRV-Cross.</summary>
internal static unsafe partial class WebShaderCompiler
{
    private const string Library = "spirv-cross-c-shared";
    private const uint Location = 30;

    public static string Transpile(byte[] desktopSpirv, ShaderCross.ShaderStage stage, bool flipVertexY = false)
    {
        // texture() becomes textureLod(..., 0.0), and min/max/clamp lose their isnan tests: both keep ANGLE's
        // Direct3D compile (FXC) from stalling. ExplicitLodRewrite and NanFreeMinMaxRewrite say why each is safe.
        byte[] spirv = NanFreeMinMaxRewrite.Apply(ExplicitLodRewrite.Apply(desktopSpirv));
        Check(ContextCreate(out nint context), 0);
        try
        {
            fixed (byte* bytes = spirv)
            {
                Check(Parse(context, bytes, (nuint)spirv.Length / 4, out nint ir), context);
                Check(CreateCompiler(context, 1, ir, 1, out nint compiler), context);
                Check(CreateOptions(compiler, out nint options), context);
                Check(SetUInt(options, 0x2000008, 300), context); // GLSL_VERSION
                Check(SetBool(options, 0x2000009, 1), context); // GLSL_ES
                // GLSL ES fragment shaders default to mediump, which phones may give 16 bits: too few for the
                // procedural materials' noise and for world coordinates. HLSL float is 32 bits; keep it so.
                Check(SetBool(options, 0x200000B, 1), context); // GLSL_ES_DEFAULT_FLOAT_PRECISION_HIGHP
                Check(SetBool(options, 0x1000004, flipVertexY ? (byte)1 : (byte)0), context); // FLIP_VERTEX_Y
                Check(InstallOptions(compiler, options), context);
                Check(Resources(compiler, out nint resources), context);

                // WebGL links varyings by name, while Vulkan links them by location.
                RenameInterfaces(compiler, resources, 3, stage == ShaderCross.ShaderStage.Vertex ? "a" : "v", context);
                RenameInterfaces(compiler, resources, 4, stage == ShaderCross.ShaderStage.Vertex ? "v" : "frag", context);
                Check(ResourceList(resources, 1, out Resource* buffers, out nuint bufferCount), context);
                for (nuint i = 0; i < bufferCount; i++)
                {
                    SetName(compiler, buffers[i].BaseType, "PostParamsBlock");
                    SetName(compiler, buffers[i].Id, "post");
                }

                Check(DummySampler(compiler, out _), context);
                Check(BuildSamplers(compiler), context);
                Check(GetSamplers(compiler, out CombinedSampler* samplers, out nuint samplerCount), context);
                for (nuint i = 0; i < samplerCount; i++)
                {
                    string name = Marshal.PtrToStringUTF8(GetName(compiler, samplers[i].Image))!;
                    SetName(compiler, samplers[i].Combined, name + "Sampler");
                }
                Check(Compile(compiler, out nint result), context);
                return Marshal.PtrToStringUTF8(result) ?? throw new InvalidOperationException("Empty GLSL output.");
            }
        }
        finally
        {
            ContextDestroy(context);
        }
    }

    private static void RenameInterfaces(nint compiler, nint resources, int type, string prefix, nint context)
    {
        Check(ResourceList(resources, type, out Resource* list, out nuint count), context);
        for (nuint i = 0; i < count; i++)
            SetName(compiler, list[i].Id, prefix + GetDecoration(compiler, list[i].Id, Location));
    }

    private static void Check(int result, nint context)
    {
        if (result != 0)
            throw new InvalidOperationException("SPIRV-Cross: " + (context == 0 ? result.ToString(System.Globalization.CultureInfo.InvariantCulture) :
                Marshal.PtrToStringUTF8(LastError(context))));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Resource
    {
        public uint Id, BaseType, Type;
        public nint Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CombinedSampler
    {
        public uint Combined, Image, Sampler;
    }

    [LibraryImport(Library, EntryPoint = "spvc_context_create")]
    private static partial int ContextCreate(out nint context);
    [LibraryImport(Library, EntryPoint = "spvc_context_destroy")]
    private static partial void ContextDestroy(nint context);
    [LibraryImport(Library, EntryPoint = "spvc_context_get_last_error_string")]
    private static partial nint LastError(nint context);
    [LibraryImport(Library, EntryPoint = "spvc_context_parse_spirv")]
    private static partial int Parse(nint context, byte* bytes, nuint words, out nint ir);
    [LibraryImport(Library, EntryPoint = "spvc_context_create_compiler")]
    private static partial int CreateCompiler(nint context, int backend, nint ir, int capture, out nint compiler);
    [LibraryImport(Library, EntryPoint = "spvc_compiler_create_compiler_options")]
    private static partial int CreateOptions(nint compiler, out nint options);
    [LibraryImport(Library, EntryPoint = "spvc_compiler_options_set_uint")]
    private static partial int SetUInt(nint options, uint option, uint value);
    [LibraryImport(Library, EntryPoint = "spvc_compiler_options_set_bool")]
    private static partial int SetBool(nint options, uint option, byte value);
    [LibraryImport(Library, EntryPoint = "spvc_compiler_install_compiler_options")]
    private static partial int InstallOptions(nint compiler, nint options);
    [LibraryImport(Library, EntryPoint = "spvc_compiler_create_shader_resources")]
    private static partial int Resources(nint compiler, out nint resources);
    [LibraryImport(Library, EntryPoint = "spvc_resources_get_resource_list_for_type")]
    private static partial int ResourceList(nint resources, int type, out Resource* list, out nuint count);
    [LibraryImport(Library, EntryPoint = "spvc_compiler_get_decoration")]
    private static partial uint GetDecoration(nint compiler, uint id, uint decoration);
    [LibraryImport(Library, EntryPoint = "spvc_compiler_set_name", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void SetName(nint compiler, uint id, string name);
    [LibraryImport(Library, EntryPoint = "spvc_compiler_get_name")]
    private static partial nint GetName(nint compiler, uint id);
    [LibraryImport(Library, EntryPoint = "spvc_compiler_build_dummy_sampler_for_combined_images")]
    private static partial int DummySampler(nint compiler, out uint id);
    [LibraryImport(Library, EntryPoint = "spvc_compiler_build_combined_image_samplers")]
    private static partial int BuildSamplers(nint compiler);
    [LibraryImport(Library, EntryPoint = "spvc_compiler_get_combined_image_samplers")]
    private static partial int GetSamplers(nint compiler, out CombinedSampler* list, out nuint count);
    [LibraryImport(Library, EntryPoint = "spvc_compiler_compile")]
    private static partial int Compile(nint compiler, out nint source);
}
