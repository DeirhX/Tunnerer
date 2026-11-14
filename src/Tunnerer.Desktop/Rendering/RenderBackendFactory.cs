namespace Tunnerer.Desktop.Rendering;

using Silk.NET.SDL;
using Tunnerer.Desktop.Config;
using Tunnerer.Desktop.Rendering.Dx11;

public static unsafe class RenderBackendFactory
{
    public readonly record struct RenderBackendServices(
        IGameRenderBackend Backend,
        ITextureLoader Textures);

    public static RenderBackendServices CreateServices(RenderBackendKind kind, Sdl sdl, Window* window)
    {
        EnsureBackendSupported(kind);
        return kind switch
        {
            RenderBackendKind.Dx11 => CreateDx11Services(sdl, window),
            _ => throw new NotSupportedException($"Unknown render backend '{kind}'."),
        };
    }

    private static void EnsureBackendSupported(RenderBackendKind kind)
    {
        if (kind == RenderBackendKind.Dx11 && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException($"{kind} backend requires Windows.");
    }

    private static RenderBackendServices CreateDx11Services(Sdl sdl, Window* window)
    {
        var backend = new Backend(sdl, window);
        return new RenderBackendServices(
            Backend: backend,
            Textures: new TextureManager(backend));
    }
}
