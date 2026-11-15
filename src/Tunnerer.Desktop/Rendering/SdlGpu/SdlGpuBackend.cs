namespace Tunnerer.Desktop.Rendering.SdlGpu;

using ImGuiNET;
using SDL3;
using Tunnerer.Core.Types;

/// <summary>
/// The SDL_GPU render backend: Metal on macOS, Direct3D 12 or Vulkan on Windows, Vulkan on Linux. It draws HLSL
/// compiled per API at build time by tools/Tunnerer.ShaderCompiler.
/// </summary>
/// <remarks>
/// One command buffer per frame. <see cref="UploadGamePixels"/> records the uploads and the terrain, post and entity
/// passes into offscreen textures; <see cref="Render"/> composes the frame (game texture plus ImGui) into
/// <see cref="_final"/>, copies that to the window's swapchain and submits. Rendering into an offscreen texture first
/// keeps screenshots working when the window is hidden and the swapchain hands out no texture.
/// </remarks>
public sealed unsafe partial class SdlGpuBackend : IGameRenderBackend
{
    private const SDL.GPUTextureFormat ColorFormat = SDL.GPUTextureFormat.B8G8R8A8Unorm;
    private const SDL.GPUTextureUsageFlags RenderTargetUsage =
        SDL.GPUTextureUsageFlags.ColorTarget | SDL.GPUTextureUsageFlags.Sampler;

    private readonly nint _window;
    private nint _device;
    private bool _windowClaimed;

    private nint _linearClampSampler;
    private nint _blitPipeline;
    private readonly GpuTexture _final = new("final frame", ColorFormat, RenderTargetUsage);
    private readonly GpuTransferBuffer _screenshotDownload = new(SDL.GPUTransferBufferUsage.Download);
    private SdlGpuImGuiRenderer? _imgui;

    private nint _frameCommandBuffer;
    private SDL.FColor _clearColor = new() { R = 0, G = 0, B = 0, A = 1 };
    private string? _pendingScreenshotPath;
    private bool _disposed;

    internal nint Device => _device;

    public nint GameTextureId => _displayTexture;
    public Size GameTextureSize => new(Math.Max(1, _scene.Width), Math.Max(1, _scene.Height));
    public bool SupportsUi => _imgui != null;

    /// <param name="window">The SDL3 window (SDL_Window*) to present to.</param>
    public SdlGpuBackend(nint window)
    {
        _window = window;
        bool debug = string.Equals(Environment.GetEnvironmentVariable("TUNNERER_GPU_DEBUG"), "1", StringComparison.Ordinal);
        _device = SDL.CreateGPUDevice(SdlGpuShaders.ProvidedFormats, debug, null!);
        if (_device == nint.Zero)
            throw new InvalidOperationException($"SDL_CreateGPUDevice failed: {SDL.GetError()}");

        try
        {
            if (!SDL.ClaimWindowForGPUDevice(_device, _window))
                throw new InvalidOperationException($"SDL_ClaimWindowForGPUDevice failed: {SDL.GetError()}");
            _windowClaimed = true;

            var samplerInfo = new SDL.GPUSamplerCreateInfo
            {
                MinFilter = SDL.GPUFilter.Linear,
                MagFilter = SDL.GPUFilter.Linear,
                MipmapMode = SDL.GPUSamplerMipmapMode.Linear,
                AddressModeU = SDL.GPUSamplerAddressMode.ClampToEdge,
                AddressModeV = SDL.GPUSamplerAddressMode.ClampToEdge,
                AddressModeW = SDL.GPUSamplerAddressMode.ClampToEdge,
                MaxLod = 1000f,
            };
            _linearClampSampler = SDL.CreateGPUSampler(_device, in samplerInfo);
            if (_linearClampSampler == nint.Zero)
                throw new InvalidOperationException($"SDL_CreateGPUSampler failed: {SDL.GetError()}");

            _blitPipeline = CreateFullscreenPipeline("BlitPs");
            CreateScenePipelines();

            SDL.GetWindowSizeInPixels(_window, out int winW, out int winH);
            _imgui = new SdlGpuImGuiRenderer(this, ColorFormat, winW, winH);
            Console.WriteLine($"[Render] SDL_GPU ready (driver={SDL.GetGPUDeviceDriver(_device)}, {winW}x{winH}).");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void ProcessEvent(in RenderInputEvent ev)
    {
        if (SupportsUi)
            ImGuiPlatform.ProcessEvent(ev);
    }

    public void ClearFrame(Size viewportSize, Color clearColor)
    {
        _ = viewportSize;
        _clearColor = new SDL.FColor { R = clearColor.R / 255f, G = clearColor.G / 255f, B = clearColor.B / 255f, A = clearColor.A / 255f };
    }

    public void NewFrame(int windowW, int windowH, float deltaTime)
    {
        if (SupportsUi)
            ImGuiPlatform.NewFrame(windowW, windowH, deltaTime);
    }

    public void SetHeatOrb(in HeatOrbState state) => _imgui?.SetHeatOrb(state);

    public void Render()
    {
        nint cmd = BeginFrame();
        SDL.GetWindowSizeInPixels(_window, out int winW, out int winH);
        if (winW <= 0 || winH <= 0)
        {
            if (SupportsUi)
                ImGui.Render();
            SubmitFrame(screenshotPath: null);
            return;
        }

        _final.Ensure(_device, winW, winH);
        ImDrawDataPtr drawData = default;
        if (SupportsUi)
        {
            ImGui.Render();
            drawData = ImGui.GetDrawData();
            _imgui!.UploadDrawData(cmd, drawData);
        }

        var target = new SDL.GPUColorTargetInfo
        {
            Texture = _final.Handle,
            ClearColor = _clearColor,
            LoadOp = SDL.GPULoadOp.Clear,
            StoreOp = SDL.GPUStoreOp.Store,
        };
        nint pass = SDL.BeginGPURenderPass(cmd, [target], 1, nint.Zero);
        if (_displayTexture != nint.Zero)
        {
            // Match the blit to the game texture so it does not stretch into the HUD strip.
            int blitW = Math.Min(_scene.Width, winW);
            int blitH = Math.Min(_scene.Height, winH);
            DrawFullscreen(pass, _blitPipeline, blitW, blitH, [_displayTexture]);
        }
        if (SupportsUi)
            _imgui!.Draw(pass, cmd, drawData, winW, winH);
        SDL.EndGPURenderPass(pass);

        string? screenshotPath = _pendingScreenshotPath;
        _pendingScreenshotPath = null;
        if (screenshotPath != null)
            QueueScreenshotDownload(cmd);

        if (SDL.WaitAndAcquireGPUSwapchainTexture(cmd, _window, out nint swapchain, out uint swapW, out uint swapH) &&
            swapchain != nint.Zero)
        {
            var blit = new SDL.GPUBlitInfo
            {
                Source = new SDL.GPUBlitRegion { Texture = _final.Handle, W = (uint)_final.Width, H = (uint)_final.Height },
                Destination = new SDL.GPUBlitRegion { Texture = swapchain, W = swapW, H = swapH },
                LoadOp = SDL.GPULoadOp.DontCare,
                Filter = SDL.GPUFilter.Nearest,
            };
            SDL.BlitGPUTexture(cmd, in blit);
        }

        SubmitFrame(screenshotPath);
    }

    public void RequestScreenshot(string? label = null)
    {
        _pendingScreenshotPath = ScreenshotFiles.NewPath(label);
        Console.WriteLine($"[Render] Queued final-frame screenshot capture: {_pendingScreenshotPath}");
    }

    /// <summary>The command buffer for this frame, acquired on first use.</summary>
    private nint BeginFrame()
    {
        if (_frameCommandBuffer != nint.Zero)
            return _frameCommandBuffer;
        _frameCommandBuffer = SDL.AcquireGPUCommandBuffer(_device);
        if (_frameCommandBuffer == nint.Zero)
            throw new InvalidOperationException($"SDL_AcquireGPUCommandBuffer failed: {SDL.GetError()}");
        return _frameCommandBuffer;
    }

    private void SubmitFrame(string? screenshotPath)
    {
        nint cmd = _frameCommandBuffer;
        _frameCommandBuffer = nint.Zero;
        if (screenshotPath == null)
        {
            SDL.SubmitGPUCommandBuffer(cmd);
            return;
        }

        nint fence = SDL.SubmitGPUCommandBufferAndAcquireFence(cmd);
        if (fence == nint.Zero)
        {
            Console.WriteLine($"[Render] Screenshot failed: {screenshotPath} ({SDL.GetError()})");
            return;
        }
        SDL.WaitForGPUFences(_device, true, [fence], 1);
        SDL.ReleaseGPUFence(_device, fence);
        WriteScreenshot(screenshotPath);
    }

    private void QueueScreenshotDownload(nint cmd)
    {
        int bytes = _final.Width * _final.Height * 4;
        _screenshotDownload.Map(_device, bytes);
        _screenshotDownload.Unmap(_device);
        nint copy = SDL.BeginGPUCopyPass(cmd);
        var source = new SDL.GPUTextureRegion
        {
            Texture = _final.Handle,
            W = (uint)_final.Width,
            H = (uint)_final.Height,
            D = 1,
        };
        var destination = new SDL.GPUTextureTransferInfo { TransferBuffer = _screenshotDownload.Handle };
        SDL.DownloadFromGPUTexture(copy, in source, in destination);
        SDL.EndGPUCopyPass(copy);
    }

    private void WriteScreenshot(string path)
    {
        byte* pixels = (byte*)SDL.MapGPUTransferBuffer(_device, _screenshotDownload.Handle, cycle: false);
        if (pixels == null)
        {
            Console.WriteLine($"[Render] Screenshot failed: {path} ({SDL.GetError()})");
            return;
        }
        try
        {
            ScreenshotFiles.WriteBgraAsPpm(path, pixels, _final.Width, _final.Height, _final.Width * 4);
            Console.WriteLine($"[Render] Screenshot saved: {path}");
        }
        finally
        {
            SDL.UnmapGPUTransferBuffer(_device, _screenshotDownload.Handle);
        }
    }

    /// <summary>
    /// Creates a sampled RGBA8 texture and uploads <paramref name="rgba"/> into it at once (HUD sprites, the ImGui
    /// font). The caller releases it with <see cref="ReleaseTexture"/>.
    /// </summary>
    internal nint CreateTextureFromRgba(byte* rgba, int width, int height, string name)
    {
        var texture = new GpuTexture(name, SDL.GPUTextureFormat.R8G8B8A8Unorm, SDL.GPUTextureUsageFlags.Sampler);
        texture.Ensure(_device, width, height);

        var staging = new GpuTransferBuffer(SDL.GPUTransferBufferUsage.Upload);
        int bytes = width * height * 4;
        byte* dst = staging.Map(_device, bytes);
        Buffer.MemoryCopy(rgba, dst, bytes, bytes);
        staging.Unmap(_device);

        nint cmd = SDL.AcquireGPUCommandBuffer(_device);
        nint copy = SDL.BeginGPUCopyPass(cmd);
        var source = new SDL.GPUTextureTransferInfo { TransferBuffer = staging.Handle };
        var region = new SDL.GPUTextureRegion { Texture = texture.Handle, W = (uint)width, H = (uint)height, D = 1 };
        SDL.UploadToGPUTexture(copy, in source, in region, cycle: false);
        SDL.EndGPUCopyPass(copy);
        SDL.SubmitGPUCommandBuffer(cmd);
        staging.Release(_device); // SDL keeps it alive until the upload has run.
        return texture.Handle;
    }

    internal void ReleaseTexture(nint texture)
    {
        if (texture != nint.Zero && _device != nint.Zero)
            SDL.ReleaseGPUTexture(_device, texture);
    }

    /// <summary>A pipeline for the fullscreen triangle (FullscreenVs) with the given pixel shader, no blending.</summary>
    private nint CreateFullscreenPipeline(string pixelShader) =>
        CreatePipeline("FullscreenVs", pixelShader, [], [], blend: null, ColorFormat);

    internal nint CreatePipeline(
        string vertexShader,
        string pixelShader,
        ReadOnlySpan<SDL.GPUVertexBufferDescription> vertexBuffers,
        ReadOnlySpan<SDL.GPUVertexAttribute> vertexAttributes,
        SDL.GPUColorTargetBlendState? blend,
        SDL.GPUTextureFormat targetFormat)
    {
        nint vs = SdlGpuShaders.Load(_device, vertexShader);
        nint ps = SdlGpuShaders.Load(_device, pixelShader);
        try
        {
            var info = new SDL.GPUGraphicsPipelineCreateInfo
            {
                VertexShader = vs,
                FragmentShader = ps,
                PrimitiveType = SDL.GPUPrimitiveType.TriangleList,
                RasterizerState = new SDL.GPURasterizerState
                {
                    FillMode = SDL.GPUFillMode.Fill,
                    CullMode = SDL.GPUCullMode.None,
                    FrontFace = SDL.GPUFrontFace.CounterClockwise,
                },
                MultisampleState = new SDL.GPUMultisampleState { SampleCount = SDL.GPUSampleCount.SampleCount1 },
            };
            SDL.GPUColorTargetDescription[] targets =
            [
                new SDL.GPUColorTargetDescription { Format = targetFormat, BlendState = blend ?? default },
            ];
            nint pipeline = SDL.CreateGPUGraphicsPipeline(_device, in info, vertexBuffers, vertexAttributes, targets);
            if (pipeline == nint.Zero)
                throw new InvalidOperationException($"SDL_CreateGPUGraphicsPipeline failed for {vertexShader}+{pixelShader}: {SDL.GetError()}");
            return pipeline;
        }
        finally
        {
            SDL.ReleaseGPUShader(_device, vs);
            SDL.ReleaseGPUShader(_device, ps);
        }
    }

    /// <summary>Draws the fullscreen triangle with one linear-clamp sampler per texture.</summary>
    private void DrawFullscreen(nint pass, nint pipeline, int width, int height, ReadOnlySpan<nint> textures)
    {
        SDL.BindGPUGraphicsPipeline(pass, pipeline);
        var viewport = new SDL.GPUViewport { W = Math.Max(1, width), H = Math.Max(1, height), MaxDepth = 1f };
        SDL.SetGPUViewport(pass, in viewport);
        Span<SDL.GPUTextureSamplerBinding> bindings = stackalloc SDL.GPUTextureSamplerBinding[textures.Length];
        for (int i = 0; i < textures.Length; i++)
            bindings[i] = new SDL.GPUTextureSamplerBinding { Texture = textures[i], Sampler = _linearClampSampler };
        SDL.BindGPUFragmentSamplers(pass, 0, bindings, (uint)bindings.Length);
        SDL.DrawGPUPrimitives(pass, 3, 1, 0, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_device == nint.Zero)
            return;

        if (_frameCommandBuffer != nint.Zero)
        {
            SDL.CancelGPUCommandBuffer(_frameCommandBuffer);
            _frameCommandBuffer = nint.Zero;
        }
        SDL.WaitForGPUIdle(_device);

        _imgui?.Dispose();
        _imgui = null;
        ReleaseScenePipelines();
        _final.Release(_device);
        _screenshotDownload.Release(_device);
        if (_blitPipeline != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(_device, _blitPipeline);
        if (_linearClampSampler != nint.Zero) SDL.ReleaseGPUSampler(_device, _linearClampSampler);
        if (_windowClaimed) SDL.ReleaseWindowFromGPUDevice(_device, _window);
        SDL.DestroyGPUDevice(_device);
        _device = nint.Zero;
    }
}
