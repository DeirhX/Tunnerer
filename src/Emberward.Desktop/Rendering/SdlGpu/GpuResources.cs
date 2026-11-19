namespace Emberward.Desktop.Rendering.SdlGpu;

using SDL3;

/// <summary>A 2D texture that is recreated when its size changes.</summary>
internal sealed class GpuTexture
{
    private readonly string _name;
    private readonly SDL.GPUTextureFormat _format;
    private readonly SDL.GPUTextureUsageFlags _usage;

    public nint Handle { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public GpuTexture(string name, SDL.GPUTextureFormat format, SDL.GPUTextureUsageFlags usage)
    {
        _name = name;
        _format = format;
        _usage = usage;
    }

    /// <summary>Returns true when the texture was (re)created, so it holds no image yet.</summary>
    public bool Ensure(nint device, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (Handle != nint.Zero && Width == width && Height == height)
            return false;

        Release(device);
        var info = new SDL.GPUTextureCreateInfo
        {
            Type = SDL.GPUTextureType.TextureType2D,
            Format = _format,
            Usage = _usage,
            Width = (uint)width,
            Height = (uint)height,
            LayerCountOrDepth = 1,
            NumLevels = 1,
            SampleCount = SDL.GPUSampleCount.SampleCount1,
        };
        Handle = SDL.CreateGPUTexture(device, in info);
        if (Handle == nint.Zero)
            throw new InvalidOperationException($"SDL_CreateGPUTexture failed for {_name} ({width}x{height}): {SDL.GetError()}");
        SDL.SetGPUTextureName(device, Handle, _name);
        Width = width;
        Height = height;
        return true;
    }

    public void Release(nint device)
    {
        if (Handle != nint.Zero)
            SDL.ReleaseGPUTexture(device, Handle);
        Handle = nint.Zero;
        Width = Height = 0;
    }
}

/// <summary>A GPU buffer that only grows.</summary>
internal sealed class GpuBuffer
{
    private readonly string _name;
    private readonly SDL.GPUBufferUsageFlags _usage;

    public nint Handle { get; private set; }
    public int Capacity { get; private set; }

    public GpuBuffer(string name, SDL.GPUBufferUsageFlags usage)
    {
        _name = name;
        _usage = usage;
    }

    public void Ensure(nint device, int bytes)
    {
        if (Handle != nint.Zero && bytes <= Capacity)
            return;
        Release(device);
        Capacity = Math.Max(4096, bytes * 2);
        var info = new SDL.GPUBufferCreateInfo { Usage = _usage, Size = (uint)Capacity };
        Handle = SDL.CreateGPUBuffer(device, in info);
        if (Handle == nint.Zero)
            throw new InvalidOperationException($"SDL_CreateGPUBuffer failed for {_name}: {SDL.GetError()}");
        SDL.SetGPUBufferName(device, Handle, _name);
    }

    public void Release(nint device)
    {
        if (Handle != nint.Zero)
            SDL.ReleaseGPUBuffer(device, Handle);
        Handle = nint.Zero;
        Capacity = 0;
    }
}

/// <summary>
/// A CPU-visible transfer buffer that only grows. Mapping cycles it, so data still in flight from an earlier frame is
/// never overwritten.
/// </summary>
internal sealed unsafe class GpuTransferBuffer
{
    private readonly SDL.GPUTransferBufferUsage _usage;

    public nint Handle { get; private set; }
    public int Capacity { get; private set; }

    public GpuTransferBuffer(SDL.GPUTransferBufferUsage usage)
    {
        _usage = usage;
    }

    public byte* Map(nint device, int bytes)
    {
        if (Handle == nint.Zero || bytes > Capacity)
        {
            Release(device);
            Capacity = Math.Max(4096, bytes);
            var info = new SDL.GPUTransferBufferCreateInfo { Usage = _usage, Size = (uint)Capacity };
            Handle = SDL.CreateGPUTransferBuffer(device, in info);
            if (Handle == nint.Zero)
                throw new InvalidOperationException($"SDL_CreateGPUTransferBuffer failed: {SDL.GetError()}");
        }
        return (byte*)SDL.MapGPUTransferBuffer(device, Handle, cycle: true);
    }

    public void Unmap(nint device) => SDL.UnmapGPUTransferBuffer(device, Handle);

    public void Release(nint device)
    {
        if (Handle != nint.Zero)
            SDL.ReleaseGPUTransferBuffer(device, Handle);
        Handle = nint.Zero;
        Capacity = 0;
    }
}
