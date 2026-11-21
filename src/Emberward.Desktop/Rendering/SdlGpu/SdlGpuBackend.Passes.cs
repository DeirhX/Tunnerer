namespace Emberward.Desktop.Rendering.SdlGpu;

using System.Runtime.CompilerServices;
using ImGuiNET;
using SDL3;
using Emberward.Core.Types;

/// <summary>
/// The game frame: uploads, then the native terrain pass (TerrainPs into <see cref="_scene"/>), the post pass
/// (PostPs into <see cref="_post"/>), the entity layer (EntityVs/EntityPs, premultiplied alpha, onto
/// <see cref="_post"/>) and, only while one shows, the whole-screen effects (ScreenFxPs into <see cref="_screen"/>).
/// </summary>
public sealed unsafe partial class SdlGpuBackend
{
    private nint _terrainPipeline;
    private nint _postPipeline;
    private nint _entityPipeline;
    private nint _screenFxPipeline;
    private readonly GpuTexture _scene = new("scene", ColorFormat, RenderTargetUsage);
    private readonly GpuTexture _post = new("post", ColorFormat, RenderTargetUsage);
    private readonly GpuTexture _screen = new("screen effects", ColorFormat, RenderTargetUsage);
    private readonly GpuTexture _nativeSource = new("native source", ColorFormat, SDL.GPUTextureUsageFlags.Sampler);
    private readonly GpuTransferBuffer _pixelUpload = new(SDL.GPUTransferBufferUsage.Upload);
    private readonly WorldDataTexture _terrainAux = new("terrain aux");
    private readonly WorldDataTexture _materialCoverage = new("material coverage");
    private readonly WorldDataTexture _fog = new("fog of war");
    private readonly GpuBuffer _entityVertices = new("entity vertices", SDL.GPUBufferUsageFlags.Vertex);
    private readonly GpuTransferBuffer _entityUpload = new(SDL.GPUTransferBufferUsage.Upload);
    private readonly List<PendingTextureUpload> _pendingTextureUploads = [];
    private int _pendingEntityBytes;
    private nint _displayTexture;
    private nint _emptyTexture;

    private readonly record struct PendingTextureUpload(nint TransferBuffer, nint Texture, int X, int Y, int W, int H);

    /// <summary>
    /// A world-sized RGBA8 texture (one texel per terrain cell), created from the full CPU array and then patched
    /// with dirty rectangles: the terrain aux texture (t1), the material coverage texture (t2) and the fog of war
    /// texture (t3, post pass only).
    /// </summary>
    private sealed class WorldDataTexture(string name)
    {
        public readonly GpuTexture Texture = new(name, SDL.GPUTextureFormat.R8G8B8A8Unorm, SDL.GPUTextureUsageFlags.Sampler);
        public readonly GpuTransferBuffer Upload = new(SDL.GPUTransferBufferUsage.Upload);

        public void Release(nint device)
        {
            Texture.Release(device);
            Upload.Release(device);
        }
    }

    /// <summary>
    /// The entity vertex layout. Must match <see cref="EntityVertex"/> (checked by EntityLayoutGuardTests) and VSIn in
    /// EntityVs.hlsl.
    /// </summary>
    public static readonly SDL.GPUVertexAttribute[] EntityVertexAttributes =
    [
        new() { Location = 0, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float2, Offset = 0 },
        new() { Location = 1, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float2, Offset = 8 },
        new() { Location = 2, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float4, Offset = 16 },
        new() { Location = 3, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float4, Offset = 32 },
        new() { Location = 4, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float2, Offset = 48 },
    ];

    private void CreateScenePipelines()
    {
        uint transparent = 0;
        _emptyTexture = CreateTextureFromRgba((byte*)&transparent, 1, 1, "empty");
        _terrainPipeline = CreateFullscreenPipeline("TerrainPs");
        _postPipeline = CreateFullscreenPipeline("PostPs");
        _screenFxPipeline = CreateFullscreenPipeline("ScreenFxPs");

        SDL.GPUVertexBufferDescription[] buffers =
        [
            new() { Slot = 0, Pitch = (uint)Unsafe.SizeOf<EntityVertex>(), InputRate = SDL.GPUVertexInputRate.Vertex },
        ];
        // Premultiplied alpha, so opaque bodies and additive glows share the pass; destination alpha is kept.
        var blend = new SDL.GPUColorTargetBlendState
        {
            EnableBlend = true,
            SrcColorBlendFactor = SDL.GPUBlendFactor.One,
            DstColorBlendFactor = SDL.GPUBlendFactor.OneMinusSrcAlpha,
            ColorBlendOp = SDL.GPUBlendOp.Add,
            SrcAlphaBlendFactor = SDL.GPUBlendFactor.Zero,
            DstAlphaBlendFactor = SDL.GPUBlendFactor.One,
            AlphaBlendOp = SDL.GPUBlendOp.Add,
        };
        _entityPipeline = CreatePipeline("EntityVs", "EntityPs", buffers, EntityVertexAttributes, blend, ColorFormat);
    }

    private void ReleaseScenePipelines()
    {
        if (_terrainPipeline != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(_device, _terrainPipeline);
        if (_postPipeline != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(_device, _postPipeline);
        if (_entityPipeline != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(_device, _entityPipeline);
        if (_screenFxPipeline != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(_device, _screenFxPipeline);
        _terrainPipeline = _postPipeline = _entityPipeline = _screenFxPipeline = nint.Zero;
        ReleaseTexture(_emptyTexture);
        _emptyTexture = nint.Zero;
        _scene.Release(_device);
        _post.Release(_device);
        _screen.Release(_device);
        _nativeSource.Release(_device);
        _pixelUpload.Release(_device);
        _terrainAux.Release(_device);
        _materialCoverage.Release(_device);
        _fog.Release(_device);
        _entityVertices.Release(_device);
        _entityUpload.Release(_device);
    }

    public void UploadGamePixels(in GamePixelsUpload upload)
    {
        int w = upload.View.ViewSize.X;
        int h = upload.View.ViewSize.Y;
        nint cmd = BeginFrame();
        _scene.Ensure(_device, w, h);
        _post.Ensure(_device, w, h);

        // ---- Stage every upload in transfer buffers, then copy them in one pass --------------------------------
        StageWorldData(_terrainAux, upload.TerrainAux.Data, upload.View.WorldSize, upload.TerrainAux.DirtyRect);
        StageWorldData(_materialCoverage, upload.TerrainAux.MaterialCoverage, upload.View.WorldSize,
            upload.TerrainAux.MaterialCoverageDirtyRect);
        StageWorldData(_fog, upload.Fog.Data, upload.View.WorldSize, upload.Fog.DirtyRect);

        bool useNativeContinuous = upload.NativeContinuous.SourcePixels != null &&
                                   upload.View.WorldSize.X > 0 &&
                                   upload.View.WorldSize.Y > 0;
        if (useNativeContinuous)
        {
            int worldW = upload.View.WorldSize.X, worldH = upload.View.WorldSize.Y;
            bool created = _nativeSource.Ensure(_device, worldW, worldH);
            if (created || upload.NativeContinuous.SourceChanged)
                StagePixels(_nativeSource, upload.NativeContinuous.SourcePixels!, worldW, worldH);
        }
        StageEntityVertices(upload.Entities);
        FlushUploads(cmd);

        // ---- Passes ------------------------------------------------------------------------------------------
        PostParamsBlock postParams = PostParamsBlock.Build(upload, upload.PostProcess.TimeSeconds);

        if (useNativeContinuous)
        {
            nint terrainPass = BeginPass(cmd, _scene.Handle, SDL.GPULoadOp.DontCare);
            SDL.PushGPUFragmentUniformData(cmd, 0, (nint)(&postParams), (uint)sizeof(PostParamsBlock));
            DrawFullscreen(terrainPass, _terrainPipeline, _scene.Width, _scene.Height,
                [_nativeSource.Handle, AuxTextureOrFallback(_terrainAux), AuxTextureOrFallback(_materialCoverage)]);
            SDL.EndGPURenderPass(terrainPass);
        }

        nint postPass = BeginPass(cmd, _post.Handle, SDL.GPULoadOp.DontCare);
        SDL.PushGPUFragmentUniformData(cmd, 0, (nint)(&postParams), (uint)sizeof(PostParamsBlock));
        DrawFullscreen(postPass, _postPipeline, _post.Width, _post.Height,
            [_scene.Handle, AuxTextureOrFallback(_terrainAux), AuxTextureOrFallback(_materialCoverage),
             AuxTextureOrFallback(_fog)]);
        DrawEntities(cmd, postPass, upload.Entities.VertexCount, postParams);
        SDL.EndGPURenderPass(postPass);
        _displayTexture = _post.Handle;

        // Whole-screen effects cost a pass only while one shows.
        if (upload.ScreenEffects.IsActive)
        {
            _screen.Ensure(_device, w, h);
            nint screenPass = BeginPass(cmd, _screen.Handle, SDL.GPULoadOp.DontCare);
            SDL.PushGPUFragmentUniformData(cmd, 0, (nint)(&postParams), (uint)sizeof(PostParamsBlock));
            DrawFullscreen(screenPass, _screenFxPipeline, _screen.Width, _screen.Height, [_post.Handle]);
            SDL.EndGPURenderPass(screenPass);
            _displayTexture = _screen.Handle;
        }
    }

    /// <summary>
    /// SDL_GPU needs a texture in every sampler slot. Before the first aux upload there is none, so a 1x1 empty
    /// texture stands in; the shaders ignore aux data then (UseTerrainAux = 0), and fog without data
    /// (PostFogEnabled = 0).
    /// </summary>
    private nint AuxTextureOrFallback(WorldDataTexture data) =>
        data.Texture.Handle != nint.Zero ? data.Texture.Handle : _emptyTexture;

    private static nint BeginPass(nint cmd, nint texture, SDL.GPULoadOp loadOp)
    {
        var target = new SDL.GPUColorTargetInfo
        {
            Texture = texture,
            LoadOp = loadOp,
            StoreOp = SDL.GPUStoreOp.Store,
        };
        return SDL.BeginGPURenderPass(cmd, [target], 1, nint.Zero);
    }

    private void StageWorldData(WorldDataTexture target, byte[]? data, Size worldSize, Rect? dirtyRect)
    {
        int worldW = worldSize.X;
        int worldH = worldSize.Y;
        if (data == null || worldW <= 0 || worldH <= 0)
            return;

        int minX, minY, maxX, maxY;
        if (target.Texture.Ensure(_device, worldW, worldH))
        {
            minX = 0; minY = 0; maxX = worldW - 1; maxY = worldH - 1;
        }
        else if (dirtyRect is Rect rect)
        {
            RectMath.GetMinMaxInclusive(rect, out minX, out minY, out maxX, out maxY);
        }
        else
        {
            return;
        }

        int rw = maxX - minX + 1;
        int rh = maxY - minY + 1;
        if (rw <= 0 || rh <= 0)
            return;

        byte* dst = target.Upload.Map(_device, rw * rh * 4);
        fixed (byte* src = data)
        {
            for (int y = 0; y < rh; y++)
            {
                long srcOffset = ((long)(minY + y) * worldW + minX) * 4;
                Buffer.MemoryCopy(src + srcOffset, dst + (long)y * rw * 4, rw * 4, rw * 4);
            }
        }
        target.Upload.Unmap(_device);
        _pendingTextureUploads.Add(new PendingTextureUpload(target.Upload.Handle, target.Texture.Handle, minX, minY, rw, rh));
    }

    private void StagePixels(GpuTexture target, uint[] pixels, int width, int height)
    {
        int bytes = width * height * 4;
        if (pixels.Length * 4 < bytes)
            return;
        byte* dst = _pixelUpload.Map(_device, bytes);
        fixed (uint* src = pixels)
            Buffer.MemoryCopy(src, dst, bytes, bytes);
        _pixelUpload.Unmap(_device);
        _pendingTextureUploads.Add(new PendingTextureUpload(_pixelUpload.Handle, target.Handle, 0, 0, width, height));
    }

    private void StageEntityVertices(in EntityLayerUpload entities)
    {
        _pendingEntityBytes = 0;
        int count = entities.VertexCount;
        if (count <= 0 || entities.Vertices == null)
            return;
        int bytes = count * Unsafe.SizeOf<EntityVertex>();
        _entityVertices.Ensure(_device, bytes);
        byte* dst = _entityUpload.Map(_device, bytes);
        fixed (EntityVertex* src = entities.Vertices)
            Buffer.MemoryCopy(src, dst, bytes, bytes);
        _entityUpload.Unmap(_device);
        _pendingEntityBytes = bytes;
    }

    private void FlushUploads(nint cmd)
    {
        if (_pendingTextureUploads.Count == 0 && _pendingEntityBytes == 0)
            return;

        nint copy = SDL.BeginGPUCopyPass(cmd);
        foreach (var pending in _pendingTextureUploads)
        {
            var source = new SDL.GPUTextureTransferInfo { TransferBuffer = pending.TransferBuffer };
            var region = new SDL.GPUTextureRegion
            {
                Texture = pending.Texture,
                X = (uint)pending.X,
                Y = (uint)pending.Y,
                W = (uint)pending.W,
                H = (uint)pending.H,
                D = 1,
            };
            // No cycling: dirty-rect uploads must keep the rest of the texture.
            SDL.UploadToGPUTexture(copy, in source, in region, cycle: false);
        }
        if (_pendingEntityBytes > 0)
        {
            var source = new SDL.GPUTransferBufferLocation { TransferBuffer = _entityUpload.Handle };
            var region = new SDL.GPUBufferRegion { Buffer = _entityVertices.Handle, Size = (uint)_pendingEntityBytes };
            SDL.UploadToGPUBuffer(copy, in source, in region, cycle: true);
        }
        SDL.EndGPUCopyPass(copy);
        _pendingTextureUploads.Clear();
    }

    /// <summary>Draws the entity quads into the post-processed frame.</summary>
    private void DrawEntities(nint cmd, nint pass, int vertexCount, PostParamsBlock postParams)
    {
        if (vertexCount <= 0 || _pendingEntityBytes == 0)
            return;
        // The vertex shader reads the same block as the pixel shaders: StepAlpha and the camera place every quad.
        SDL.PushGPUVertexUniformData(cmd, 0, (nint)(&postParams), (uint)sizeof(PostParamsBlock));
        SDL.BindGPUGraphicsPipeline(pass, _entityPipeline);
        var viewport = new SDL.GPUViewport { W = _post.Width, H = _post.Height, MaxDepth = 1f };
        SDL.SetGPUViewport(pass, in viewport);
        SDL.GPUBufferBinding[] binding = [new SDL.GPUBufferBinding { Buffer = _entityVertices.Handle }];
        SDL.BindGPUVertexBuffers(pass, 0, binding, 1);
        SDL.DrawGPUPrimitives(pass, (uint)vertexCount, 1, 0, 0);
    }
}
