namespace Tunnerer.Desktop.Rendering.SdlGpu;

using System.Numerics;
using System.Runtime.CompilerServices;
using ImGuiNET;
using SDL3;

/// <summary>
/// Draws Dear ImGui with SDL_GPU (ImGuiVs/ImGuiPs). Context and input handling are in <see cref="ImGuiPlatform"/>.
/// ImGui texture IDs are SDL_GPUTexture pointers.
/// </summary>
internal sealed unsafe class SdlGpuImGuiRenderer : IDisposable
{
    private readonly SdlGpuBackend _backend;
    private readonly nint _device;
    private readonly nint _pipeline;
    private readonly nint _heatOrbPipeline;
    private HeatOrbUniforms _heatOrb;
    private readonly nint _sampler;
    private readonly nint _fontTexture;
    private readonly GpuBuffer _vertices = new("imgui vertices", SDL.GPUBufferUsageFlags.Vertex);
    private readonly GpuBuffer _indices = new("imgui indices", SDL.GPUBufferUsageFlags.Index);
    private readonly GpuTransferBuffer _upload = new(SDL.GPUTransferBufferUsage.Upload);
    private bool _disposed;

    private struct Transform
    {
        public Vector2 Scale;
        public Vector2 Translate;
    }

    // Must match the HeatOrbParams cbuffer in HeatOrbPs.hlsl.
    private struct HeatOrbUniforms
    {
        public Vector4 LiquidColor;
        public Vector4 State;
    }

    public SdlGpuImGuiRenderer(SdlGpuBackend backend, SDL.GPUTextureFormat targetFormat, int windowW, int windowH)
    {
        _backend = backend;
        _device = backend.Device;
        ImGuiPlatform.CreateContext(windowW, windowH);

        // Must match ImDrawVert and VSIn in ImGuiVs.hlsl.
        SDL.GPUVertexBufferDescription[] buffers =
        [
            new() { Slot = 0, Pitch = (uint)Unsafe.SizeOf<ImDrawVert>(), InputRate = SDL.GPUVertexInputRate.Vertex },
        ];
        SDL.GPUVertexAttribute[] attributes =
        [
            new() { Location = 0, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float2, Offset = 0 },
            new() { Location = 1, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float2, Offset = 8 },
            new() { Location = 2, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Ubyte4Norm, Offset = 16 },
        ];
        var blend = new SDL.GPUColorTargetBlendState
        {
            EnableBlend = true,
            SrcColorBlendFactor = SDL.GPUBlendFactor.SrcAlpha,
            DstColorBlendFactor = SDL.GPUBlendFactor.OneMinusSrcAlpha,
            ColorBlendOp = SDL.GPUBlendOp.Add,
            SrcAlphaBlendFactor = SDL.GPUBlendFactor.One,
            DstAlphaBlendFactor = SDL.GPUBlendFactor.OneMinusSrcAlpha,
            AlphaBlendOp = SDL.GPUBlendOp.Add,
        };
        _pipeline = backend.CreatePipeline("ImGuiVs", "ImGuiPs", buffers, attributes, blend, targetFormat);
        _heatOrbPipeline = backend.CreatePipeline("ImGuiVs", "HeatOrbPs", buffers, attributes, blend, targetFormat);

        var samplerInfo = new SDL.GPUSamplerCreateInfo
        {
            MinFilter = SDL.GPUFilter.Linear,
            MagFilter = SDL.GPUFilter.Linear,
            MipmapMode = SDL.GPUSamplerMipmapMode.Linear,
            AddressModeU = SDL.GPUSamplerAddressMode.Repeat,
            AddressModeV = SDL.GPUSamplerAddressMode.Repeat,
            AddressModeW = SDL.GPUSamplerAddressMode.Repeat,
        };
        _sampler = SDL.CreateGPUSampler(_device, in samplerInfo);
        if (_sampler == nint.Zero)
            throw new InvalidOperationException($"SDL_CreateGPUSampler failed for ImGui: {SDL.GetError()}");

        var io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out int width, out int height, out _);
        _fontTexture = backend.CreateTextureFromRgba(pixels, width, height, "imgui font");
        io.Fonts.SetTexID(_fontTexture);
    }

    public void SetHeatOrb(in HeatOrbState state)
    {
        _heatOrb = new HeatOrbUniforms
        {
            LiquidColor = new Vector4(state.LiquidColor, 1f),
            State = new Vector4(state.Frozen, state.Boil, state.Burning, state.Time),
        };
    }

    /// <summary>Copies this frame's vertices and indices to the GPU. Must run outside any render pass.</summary>
    public void UploadDrawData(nint cmd, ImDrawDataPtr drawData)
    {
        if (drawData.CmdListsCount <= 0 || drawData.TotalVtxCount <= 0)
            return;

        int vertexBytes = drawData.TotalVtxCount * Unsafe.SizeOf<ImDrawVert>();
        int indexBytes = drawData.TotalIdxCount * sizeof(ushort);
        _vertices.Ensure(_device, vertexBytes);
        _indices.Ensure(_device, indexBytes);

        byte* dst = _upload.Map(_device, vertexBytes + indexBytes);
        byte* vtxDst = dst;
        byte* idxDst = dst + vertexBytes;
        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            var cmdList = drawData.CmdLists[n];
            long vtx = (long)cmdList.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>();
            long idx = (long)cmdList.IdxBuffer.Size * sizeof(ushort);
            Buffer.MemoryCopy((void*)cmdList.VtxBuffer.Data, vtxDst, vtx, vtx);
            Buffer.MemoryCopy((void*)cmdList.IdxBuffer.Data, idxDst, idx, idx);
            vtxDst += vtx;
            idxDst += idx;
        }
        _upload.Unmap(_device);

        nint copy = SDL.BeginGPUCopyPass(cmd);
        var vertexSource = new SDL.GPUTransferBufferLocation { TransferBuffer = _upload.Handle, Offset = 0 };
        var vertexRegion = new SDL.GPUBufferRegion { Buffer = _vertices.Handle, Size = (uint)vertexBytes };
        SDL.UploadToGPUBuffer(copy, in vertexSource, in vertexRegion, cycle: true);
        var indexSource = new SDL.GPUTransferBufferLocation { TransferBuffer = _upload.Handle, Offset = (uint)vertexBytes };
        var indexRegion = new SDL.GPUBufferRegion { Buffer = _indices.Handle, Size = (uint)indexBytes };
        SDL.UploadToGPUBuffer(copy, in indexSource, in indexRegion, cycle: true);
        SDL.EndGPUCopyPass(copy);
    }

    /// <summary>Draws the uploaded data into the current render pass.</summary>
    public void Draw(nint pass, nint cmd, ImDrawDataPtr drawData, int fbWidth, int fbHeight)
    {
        if (drawData.CmdListsCount <= 0 || drawData.TotalVtxCount <= 0 || fbWidth <= 0 || fbHeight <= 0)
            return;

        SDL.BindGPUGraphicsPipeline(pass, _pipeline);
        var viewport = new SDL.GPUViewport { W = fbWidth, H = fbHeight, MaxDepth = 1f };
        SDL.SetGPUViewport(pass, in viewport);

        var transform = new Transform
        {
            Scale = new Vector2(2f / drawData.DisplaySize.X, -2f / drawData.DisplaySize.Y),
        };
        transform.Translate = new Vector2(
            -1f - drawData.DisplayPos.X * transform.Scale.X,
            1f - drawData.DisplayPos.Y * transform.Scale.Y);
        SDL.PushGPUVertexUniformData(cmd, 0, (nint)(&transform), (uint)sizeof(Transform));

        SDL.GPUBufferBinding[] vertexBinding = [new SDL.GPUBufferBinding { Buffer = _vertices.Handle }];
        SDL.BindGPUVertexBuffers(pass, 0, vertexBinding, 1);
        var indexBinding = new SDL.GPUBufferBinding { Buffer = _indices.Handle };
        SDL.BindGPUIndexBuffer(pass, in indexBinding, SDL.GPUIndexElementSize.IndexElementSize16Bit);

        Span<SDL.GPUTextureSamplerBinding> textureBinding = stackalloc SDL.GPUTextureSamplerBinding[1];
        Vector2 clipOff = drawData.DisplayPos;
        Vector2 clipScale = drawData.FramebufferScale;
        int globalVtxOffset = 0;
        int globalIdxOffset = 0;
        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            var cmdList = drawData.CmdLists[n];
            for (int cmdi = 0; cmdi < cmdList.CmdBuffer.Size; cmdi++)
            {
                var pcmd = cmdList.CmdBuffer[cmdi];
                if (pcmd.UserCallback != nint.Zero)
                    continue;

                float x0 = Math.Max(0f, (pcmd.ClipRect.X - clipOff.X) * clipScale.X);
                float y0 = Math.Max(0f, (pcmd.ClipRect.Y - clipOff.Y) * clipScale.Y);
                float x1 = Math.Min(fbWidth, (pcmd.ClipRect.Z - clipOff.X) * clipScale.X);
                float y1 = Math.Min(fbHeight, (pcmd.ClipRect.W - clipOff.Y) * clipScale.Y);
                if (x1 <= x0 || y1 <= y0)
                    continue;

                var scissor = new SDL.Rect { X = (int)x0, Y = (int)y0, W = (int)(x1 - x0), H = (int)(y1 - y0) };
                SDL.SetGPUScissor(pass, in scissor);

                if (pcmd.TextureId == HeatOrbEffect.TextureId)
                {
                    // The temperature orb is a shader, not a texture: swap pipelines for this draw.
                    SDL.BindGPUGraphicsPipeline(pass, _heatOrbPipeline);
                    SDL.PushGPUVertexUniformData(cmd, 0, (nint)(&transform), (uint)sizeof(Transform));
                    HeatOrbUniforms orb = _heatOrb;
                    SDL.PushGPUFragmentUniformData(cmd, 0, (nint)(&orb), (uint)sizeof(HeatOrbUniforms));
                    SDL.DrawGPUIndexedPrimitives(
                        pass, pcmd.ElemCount, 1,
                        (uint)(pcmd.IdxOffset + globalIdxOffset),
                        (int)(pcmd.VtxOffset + globalVtxOffset),
                        0);
                    SDL.BindGPUGraphicsPipeline(pass, _pipeline);
                    SDL.PushGPUVertexUniformData(cmd, 0, (nint)(&transform), (uint)sizeof(Transform));
                    continue;
                }

                textureBinding[0] = new SDL.GPUTextureSamplerBinding { Texture = pcmd.TextureId, Sampler = _sampler };
                SDL.BindGPUFragmentSamplers(pass, 0, textureBinding, 1);
                SDL.DrawGPUIndexedPrimitives(
                    pass, pcmd.ElemCount, 1,
                    (uint)(pcmd.IdxOffset + globalIdxOffset),
                    (int)(pcmd.VtxOffset + globalVtxOffset),
                    0);
            }
            globalIdxOffset += cmdList.IdxBuffer.Size;
            globalVtxOffset += cmdList.VtxBuffer.Size;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ImGuiPlatform.DestroyContext();
        _vertices.Release(_device);
        _indices.Release(_device);
        _upload.Release(_device);
        _backend.ReleaseTexture(_fontTexture);
        SDL.ReleaseGPUSampler(_device, _sampler);
        SDL.ReleaseGPUGraphicsPipeline(_device, _pipeline);
        SDL.ReleaseGPUGraphicsPipeline(_device, _heatOrbPipeline);
    }
}
