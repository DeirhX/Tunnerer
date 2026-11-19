namespace Emberward.ShaderCompiler;

using System.Runtime.InteropServices;

/// <summary>
/// The parts of SDL_shadercross 3.0 (include/SDL3_shadercross/SDL_shadercross.h) this tool uses.
/// Struct layouts must match that header.
/// </summary>
internal static unsafe partial class ShaderCross
{
    private const string Library = "SDL3_shadercross";
    private const string SdlLibrary = "SDL3";

    public enum ShaderStage : int
    {
        Vertex = 0,
        Fragment = 1,
        Compute = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HlslDefine
    {
        public byte* Name;
        public byte* Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HlslInfo
    {
        public byte* Source;
        public byte* Entrypoint;
        public byte* IncludeDir;
        public HlslDefine* Defines;
        public ShaderStage ShaderStage;
        public uint Props;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SpirvInfo
    {
        public byte* Bytecode;
        public nuint BytecodeSize;
        public byte* Entrypoint;
        public ShaderStage ShaderStage;
        public uint Props;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GraphicsShaderResourceInfo
    {
        public uint NumSamplers;
        public uint NumStorageTextures;
        public uint NumStorageBuffers;
        public uint NumUniformBuffers;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GraphicsShaderMetadata
    {
        public GraphicsShaderResourceInfo ResourceInfo;
        public uint NumInputs;
        public void* Inputs;
        public uint NumOutputs;
        public void* Outputs;
    }

    [LibraryImport(Library, EntryPoint = "SDL_ShaderCross_Init")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool Init();

    [LibraryImport(Library, EntryPoint = "SDL_ShaderCross_Quit")]
    public static partial void Quit();

    [LibraryImport(Library, EntryPoint = "SDL_ShaderCross_CompileSPIRVFromHLSL")]
    public static partial void* CompileSpirvFromHlsl(HlslInfo* info, nuint* size);

    [LibraryImport(Library, EntryPoint = "SDL_ShaderCross_CompileDXILFromHLSL")]
    public static partial void* CompileDxilFromHlsl(HlslInfo* info, nuint* size);

    [LibraryImport(Library, EntryPoint = "SDL_ShaderCross_TranspileMSLFromSPIRV")]
    public static partial void* TranspileMslFromSpirv(SpirvInfo* info);

    [LibraryImport(Library, EntryPoint = "SDL_ShaderCross_ReflectGraphicsSPIRV")]
    public static partial GraphicsShaderMetadata* ReflectGraphicsSpirv(byte* bytecode, nuint bytecodeSize, uint props);

    [LibraryImport(SdlLibrary, EntryPoint = "SDL_free")]
    public static partial void SdlFree(void* mem);

    [LibraryImport(SdlLibrary, EntryPoint = "SDL_GetError")]
    private static partial byte* SdlGetErrorNative();

    public static string LastError() => Marshal.PtrToStringUTF8((nint)SdlGetErrorNative()) ?? "(no error text)";

    /// <summary>
    /// The SDL3 packages ship the library under two names (libSDL3.dylib and libSDL3.0.dylib on macOS). shadercross
    /// links against the versioned one, so load that same file for SDL_free and SDL_GetError; otherwise two copies
    /// of SDL end up in the process.
    /// </summary>
    public static void UseVersionedSdlLibrary()
    {
        NativeLibrary.SetDllImportResolver(typeof(ShaderCross).Assembly, (name, assembly, searchPath) =>
        {
            if (name != SdlLibrary)
                return nint.Zero;
            string versioned = OperatingSystem.IsMacOS() ? "libSDL3.0.dylib"
                : OperatingSystem.IsLinux() ? "libSDL3.so.0"
                : "SDL3.dll";
            return NativeLibrary.TryLoad(versioned, assembly, searchPath, out nint handle) ? handle : nint.Zero;
        });
    }
}
