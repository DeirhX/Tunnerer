namespace Tunnerer.Desktop.Rendering;

using Tunnerer.Desktop.Config;
using Tunnerer.Desktop.Rendering.Dx11;
using Tunnerer.Desktop.Rendering.SdlGpu;

public static class RenderBackendFactory
{
    public readonly record struct RenderBackendServices(
        IGameRenderBackend Backend,
        ITextureLoader Textures);

    /// <param name="window">The SDL3 window (SDL_Window*) the backend draws into.</param>
    public static RenderBackendServices CreateServices(RenderBackendKind kind, nint window)
    {
        switch (kind)
        {
            case RenderBackendKind.Dx11:
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException($"{kind} backend requires Windows. Use --backend=sdlgpu.");
                var backend = new Backend(window);
                return new RenderBackendServices(backend, new TextureManager(backend));
            }
            case RenderBackendKind.SdlGpu:
            {
                var backend = new SdlGpuBackend(window);
                return new RenderBackendServices(backend, new SdlGpuTextureLoader(backend));
            }
            default:
                throw new NotSupportedException($"Unknown render backend '{kind}'.");
        }
    }
}
