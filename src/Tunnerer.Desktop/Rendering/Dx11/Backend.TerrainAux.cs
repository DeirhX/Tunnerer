namespace Tunnerer.Desktop.Rendering.Dx11;

using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Tunnerer.Core.Types;

public sealed unsafe partial class Backend
{
    /// <summary>
    /// A world-sized RGBA8 texture (one texel per terrain cell) that is created from a full CPU array and then
    /// patched with dirty rectangles. Used for the terrain aux texture (t1) and the material coverage texture (t2).
    /// </summary>
    private sealed class WorldDataTexture
    {
        public ID3D11Texture2D* Texture;
        public ID3D11ShaderResourceView* Srv;
        public byte[]? UploadScratch;
        public int Width, Height;

        public void Release()
        {
            if (Srv != null) { Srv->Release(); Srv = null; }
            if (Texture != null) { Texture->Release(); Texture = null; }
        }
    }

    private void UpdateWorldDataTexture(WorldDataTexture target, byte[]? data, Size worldSize, Rect? dirtyRect)
    {
        int worldW = worldSize.X;
        int worldH = worldSize.Y;
        if (data == null || worldW <= 0 || worldH <= 0)
            return;

        bool sizeChanged = target.Texture == null || target.Width != worldW || target.Height != worldH;
        if (sizeChanged)
        {
            target.Release();

            var desc = CreateTextureDesc(
                worldW, worldH, Format.FormatR8G8B8A8Unorm, Usage.Default,
                (uint)BindFlag.ShaderResource, 0);
            fixed (byte* ptr = data)
            {
                var sub = new SubresourceData
                {
                    PSysMem = ptr,
                    SysMemPitch = (uint)(worldW * 4),
                    SysMemSlicePitch = 0,
                };
                ID3D11Texture2D* tex = null;
                _device->CreateTexture2D(&desc, &sub, &tex);
                target.Texture = tex;
            }
            ID3D11ShaderResourceView* srv = null;
            _device->CreateShaderResourceView((ID3D11Resource*)target.Texture, null, &srv);
            target.Srv = srv;
            target.Width = worldW;
            target.Height = worldH;
            return;
        }

        if (dirtyRect is not Rect rect)
            return;
        RectMath.GetMinMaxInclusive(rect, out int minX, out int minY, out int maxX, out int maxY);
        int rw = maxX - minX + 1;
        int rh = maxY - minY + 1;
        if (rw <= 0 || rh <= 0) return;
        var box = new Box((uint)minX, (uint)minY, 0u, (uint)(maxX + 1), (uint)(maxY + 1), 1u);
        int required = rw * rh * 4;
        if (target.UploadScratch == null || target.UploadScratch.Length < required)
            target.UploadScratch = new byte[required];
        byte[] scratch = target.UploadScratch;
        for (int y = 0; y < rh; y++)
        {
            int src = ((minY + y) * worldW + minX) * 4;
            int dst = y * rw * 4;
            Array.Copy(data, src, scratch, dst, rw * 4);
        }
        fixed (byte* ptr = scratch)
        {
            _context->UpdateSubresource((ID3D11Resource*)target.Texture, 0, &box, ptr, (uint)(rw * 4), 0);
        }
    }

    /// <summary>Binds the terrain aux texture at t1 and the material coverage texture at t2.</summary>
    private void BindTerrainDataTextures()
    {
        ID3D11ShaderResourceView* srvs0 = _terrainAux.Srv;
        _context->PSSetShaderResources(1, 1, &srvs0);
        ID3D11ShaderResourceView* srvs1 = _materialCoverage.Srv;
        _context->PSSetShaderResources(2, 1, &srvs1);
    }

    private void UnbindTerrainDataTextures()
    {
        ID3D11ShaderResourceView* nullSrv = null;
        _context->PSSetShaderResources(1, 1, &nullSrv);
        _context->PSSetShaderResources(2, 1, &nullSrv);
    }
}
