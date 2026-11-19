namespace Emberward.Desktop.Rendering.SdlGpu;

using StbImageSharp;

/// <summary>
/// Loads PNG/JPG images from disk into SDL_GPU textures for the HUD. Returned IDs are SDL_GPUTexture pointers, which
/// <see cref="SdlGpuImGuiRenderer"/> takes as ImGui texture IDs.
/// </summary>
public sealed unsafe class SdlGpuTextureLoader : ITextureLoader
{
    private readonly SdlGpuBackend _backend;
    private readonly List<nint> _textures = [];
    private bool _disposed;

    public SdlGpuTextureLoader(SdlGpuBackend backend)
    {
        _backend = backend;
        StbImage.stbi_set_flip_vertically_on_load(0);
    }

    public nint LoadTexture(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Texture file not found: {path}", path);

        using var stream = File.OpenRead(path);
        var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        fixed (byte* pixels = image.Data)
        {
            nint texture = _backend.CreateTextureFromRgba(pixels, image.Width, image.Height, Path.GetFileName(path));
            _textures.Add(texture);
            return texture;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (nint texture in _textures)
            _backend.ReleaseTexture(texture);
        _textures.Clear();
    }
}
