namespace Emberward.Desktop.Rendering.SdlGpu;

using System.Text.Json;
using SDL3;

/// <summary>
/// Loads the shaders that tools/Emberward.ShaderCompiler wrote next to the executable (Shaders/&lt;name&gt;.msl,
/// .dxil, .spv and .json) in the format the GPU device takes.
/// </summary>
internal static class SdlGpuShaders
{
    /// <summary>Every format the build produces; the device picks its native API from these.</summary>
    public const SDL.GPUShaderFormat ProvidedFormats =
        SDL.GPUShaderFormat.MSL | SDL.GPUShaderFormat.DXIL | SDL.GPUShaderFormat.SPIRV;

    private sealed record Metadata(
        string Stage,
        uint Samplers,
        uint StorageTextures,
        uint StorageBuffers,
        uint UniformBuffers,
        string SpirvEntrypoint,
        string DxilEntrypoint,
        string MslEntrypoint);

    public static nint Load(nint device, string name)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        string metadataPath = Path.Combine(dir, name + ".json");
        if (!File.Exists(metadataPath))
            throw new FileNotFoundException($"Shader metadata missing: {metadataPath}. Was the shader build step run?", metadataPath);
        var meta = JsonSerializer.Deserialize<Metadata>(File.ReadAllText(metadataPath))
            ?? throw new InvalidDataException($"Could not read {metadataPath}.");

        SDL.GPUShaderFormat supported = SDL.GetGPUShaderFormats(device);
        (SDL.GPUShaderFormat format, string extension, string entry) =
            (supported & SDL.GPUShaderFormat.MSL) != 0 ? (SDL.GPUShaderFormat.MSL, ".msl", meta.MslEntrypoint)
            : (supported & SDL.GPUShaderFormat.DXIL) != 0 ? (SDL.GPUShaderFormat.DXIL, ".dxil", meta.DxilEntrypoint)
            : (supported & SDL.GPUShaderFormat.SPIRV) != 0 ? (SDL.GPUShaderFormat.SPIRV, ".spv", meta.SpirvEntrypoint)
            : throw new NotSupportedException($"The GPU device takes none of the shader formats the build produces ({supported}).");

        byte[] code = File.ReadAllBytes(Path.Combine(dir, name + extension));
        if (format == SDL.GPUShaderFormat.MSL)
            code = [.. code, 0]; // SDL reads MSL as a C string.

        var info = new SDL.GPUShaderCreateInfo
        {
            Format = format,
            Stage = meta.Stage == "vertex" ? SDL.GPUShaderStage.Vertex : SDL.GPUShaderStage.Fragment,
            NumSamplers = meta.Samplers,
            NumStorageTextures = meta.StorageTextures,
            NumStorageBuffers = meta.StorageBuffers,
            NumUniformBuffers = meta.UniformBuffers,
        };
        nint shader = SDL.CreateGPUShader(device, in info, code, entry);
        if (shader == nint.Zero)
            throw new InvalidOperationException($"SDL_CreateGPUShader failed for {name}{extension}: {SDL.GetError()}");
        return shader;
    }
}
