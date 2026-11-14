namespace Tunnerer.Desktop.Rendering.Dx11;

using System.Runtime.CompilerServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

/// <summary>
/// The entity layer: tanks and projectiles drawn as procedural quads at screen resolution, on top of the
/// post-processed terrain frame. Vertices come ready-made from <see cref="EntityLayerBuilder"/>.
/// </summary>
public sealed unsafe partial class Backend
{
    private ID3D11VertexShader* _entityVs;
    private ID3D11PixelShader* _entityPs;
    private ID3D11InputLayout* _entityLayout;
    private ID3D11BlendState* _entityBlend;
    private ID3D11RasterizerState* _entityRasterizer;
    private ID3D11Buffer* _entityVertexBuffer;
    private int _entityVertexCapacity;

    private bool CreateEntityPipeline()
    {
        if (!TryGetShaderBytecode("EntityVs", "vs_5_0", out byte[]? vsBytecode) || vsBytecode == null ||
            !TryGetShaderBytecode("EntityPs", "ps_5_0", out byte[]? psBytecode) || psBytecode == null)
            return false;

        ID3D11VertexShader* vs = null;
        ID3D11PixelShader* ps = null;
        ID3D11InputLayout* layout = null;
        var position = SilkMarshal.StringToPtr("POSITION");
        var texcoord = SilkMarshal.StringToPtr("TEXCOORD");
        var color = SilkMarshal.StringToPtr("COLOR");
        try
        {
            fixed (byte* pVs = vsBytecode)
            fixed (byte* pPs = psBytecode)
            {
                if (_device->CreateVertexShader(pVs, (nuint)vsBytecode.Length, null, &vs) < 0 || vs == null)
                    return false;
                _entityVs = vs;
                if (_device->CreatePixelShader(pPs, (nuint)psBytecode.Length, null, &ps) < 0 || ps == null)
                    return false;
                _entityPs = ps;

                // Must match EntityVertex and VSIn in EntityVs.hlsl.
                var elements = stackalloc InputElementDesc[4];
                elements[0] = VertexElement((byte*)position, 0, Format.FormatR32G32Float, 0);
                elements[1] = VertexElement((byte*)texcoord, 0, Format.FormatR32G32Float, 8);
                elements[2] = VertexElement((byte*)color, 0, Format.FormatR32G32B32A32Float, 16);
                elements[3] = VertexElement((byte*)texcoord, 1, Format.FormatR32G32B32A32Float, 32);
                if (_device->CreateInputLayout(elements, 4, pVs, (nuint)vsBytecode.Length, &layout) < 0 || layout == null)
                    return false;
                _entityLayout = layout;
            }
        }
        finally
        {
            SilkMarshal.Free((nint)position);
            SilkMarshal.Free((nint)texcoord);
            SilkMarshal.Free((nint)color);
        }

        // Premultiplied alpha, so opaque bodies and additive glows share the pass.
        var blendDesc = new BlendDesc { AlphaToCoverageEnable = 0, IndependentBlendEnable = 0 };
        blendDesc.RenderTarget[0].BlendEnable = 1;
        blendDesc.RenderTarget[0].SrcBlend = Blend.One;
        blendDesc.RenderTarget[0].DestBlend = Blend.InvSrcAlpha;
        blendDesc.RenderTarget[0].BlendOp = BlendOp.Add;
        blendDesc.RenderTarget[0].SrcBlendAlpha = Blend.Zero;
        blendDesc.RenderTarget[0].DestBlendAlpha = Blend.One;
        blendDesc.RenderTarget[0].BlendOpAlpha = BlendOp.Add;
        blendDesc.RenderTarget[0].RenderTargetWriteMask = (byte)ColorWriteEnable.All;
        ID3D11BlendState* blend = null;
        if (_device->CreateBlendState(&blendDesc, &blend) < 0 || blend == null)
            return false;
        _entityBlend = blend;

        var rasterDesc = new RasterizerDesc
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            ScissorEnable = 0,
            DepthClipEnable = 1,
        };
        ID3D11RasterizerState* rasterizer = null;
        if (_device->CreateRasterizerState(&rasterDesc, &rasterizer) < 0 || rasterizer == null)
            return false;
        _entityRasterizer = rasterizer;
        return true;
    }

    private static InputElementDesc VertexElement(byte* semantic, uint index, Format format, uint offset) => new()
    {
        SemanticName = semantic,
        SemanticIndex = index,
        Format = format,
        InputSlot = 0,
        AlignedByteOffset = offset,
        InputSlotClass = InputClassification.PerVertexData,
        InstanceDataStepRate = 0,
    };

    /// <summary>Draws the entity quads into the post-processed frame.</summary>
    private void RunEntityPass(in EntityLayerUpload entities)
    {
        int count = entities.VertexCount;
        if (count <= 0 || entities.Vertices == null || _postRtv == null || _entityVs == null)
            return;
        if (!EnsureEntityVertexBuffer(count))
            return;

        MappedSubresource mapped;
        if (_context->Map((ID3D11Resource*)_entityVertexBuffer, 0, Silk.NET.Direct3D11.Map.WriteDiscard, 0, &mapped) < 0)
            return;
        fixed (EntityVertex* src = entities.Vertices)
        {
            nuint bytes = (nuint)(count * Unsafe.SizeOf<EntityVertex>());
            Buffer.MemoryCopy(src, mapped.PData, bytes, bytes);
        }
        _context->Unmap((ID3D11Resource*)_entityVertexBuffer, 0);

        ID3D11RenderTargetView* rtv = _postRtv;
        _context->OMSetRenderTargets(1, &rtv, null);
        _context->OMSetBlendState(_entityBlend, null, 0xFFFFFFFFu);
        _context->OMSetDepthStencilState(null, 0);
        _context->RSSetState(_entityRasterizer);
        var viewport = new Viewport(0, 0, (uint)Math.Max(1, _sceneTexW), (uint)Math.Max(1, _sceneTexH), 0f, 1f);
        _context->RSSetViewports(1, &viewport);

        uint stride = (uint)Unsafe.SizeOf<EntityVertex>();
        uint offset = 0;
        ID3D11Buffer* vb = _entityVertexBuffer;
        _context->IASetVertexBuffers(0, 1, &vb, &stride, &offset);
        _context->IASetInputLayout(_entityLayout);
        _context->IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        _context->VSSetShader(_entityVs, null, 0);
        _context->PSSetShader(_entityPs, null, 0);
        _context->Draw((uint)count, 0);

        _context->OMSetBlendState(null, null, 0xFFFFFFFFu);
        _context->RSSetState(null);
    }

    private bool EnsureEntityVertexBuffer(int vertexCount)
    {
        if (_entityVertexBuffer != null && vertexCount <= _entityVertexCapacity)
            return true;
        if (_entityVertexBuffer != null) { _entityVertexBuffer->Release(); _entityVertexBuffer = null; }

        _entityVertexCapacity = Math.Max(1024, vertexCount * 2);
        var desc = new BufferDesc
        {
            Usage = Usage.Dynamic,
            ByteWidth = (uint)(_entityVertexCapacity * Unsafe.SizeOf<EntityVertex>()),
            BindFlags = (uint)BindFlag.VertexBuffer,
            CPUAccessFlags = (uint)CpuAccessFlag.Write,
            MiscFlags = 0,
            StructureByteStride = 0,
        };
        ID3D11Buffer* vb = null;
        if (_device->CreateBuffer(&desc, null, &vb) < 0 || vb == null)
        {
            _entityVertexCapacity = 0;
            return false;
        }
        _entityVertexBuffer = vb;
        return true;
    }

    private void ReleaseEntityPipeline()
    {
        if (_entityVertexBuffer != null) { _entityVertexBuffer->Release(); _entityVertexBuffer = null; }
        if (_entityRasterizer != null) { _entityRasterizer->Release(); _entityRasterizer = null; }
        if (_entityBlend != null) { _entityBlend->Release(); _entityBlend = null; }
        if (_entityLayout != null) { _entityLayout->Release(); _entityLayout = null; }
        if (_entityPs != null) { _entityPs->Release(); _entityPs = null; }
        if (_entityVs != null) { _entityVs->Release(); _entityVs = null; }
    }
}
