namespace Emberward.Browser.Client;

using System.Runtime.InteropServices;
using Emberward.Core.Types;
using Emberward.Desktop.Config;
using Emberward.Desktop.Rendering;

/// <summary>
/// Runs the shared desktop presentation pipeline and packs what a frame draws into one buffer; the browser only
/// uploads its parts and draws.
/// </summary>
/// <remarks>
/// The packet starts with <see cref="HeaderInts"/> little-endian int32 values, then the parts they point at, each
/// starting on a multiple of four bytes (webgl.js reads the same layout):
/// <list type="bullet">
/// <item>0, 1: offset and byte length of the shader constants (<see cref="PostParamsBlock"/>).</item>
/// <item>2, 3: offset and byte length of the entity vertices (<see cref="EntityVertex"/>).</item>
/// <item>4 + 5t .. 8 + 5t, for each terrain texture t of <see cref="BrowserTerrain"/>: offset of its texels, then the
/// x, y, width and height of the rectangle they replace. Width 0: the texture is unchanged.</item>
/// </list>
/// </remarks>
public sealed class BrowserScene
{
    public const int HeaderInts = 4 + 5 * BrowserTerrain.TextureCount;

    /// <summary>A packet buffer this much larger than a frame needs is let go (the first frame carries the whole map).</summary>
    private const int ShrinkFactor = 4;
    private const int MinPacketBytes = 64 * 1024;

    private readonly BrowserSession _session;
    private readonly EntityLayerBuilder _entities = new();
    private readonly ScreenEffects _screenEffects = new();
    private readonly TerrainAuxBuilder _glowBuilder = new();
    private readonly float[] _glows = new float[PostParamsBlock.MaxTankGlows * 4];
    private readonly int[] _header = new int[HeaderInts];
    private byte[] _packet = new byte[MinPacketBytes];
    private int _packetLength;
    private float _visualTime;

    public BrowserScene(BrowserSession session)
    {
        _session = session;
        Terrain = new BrowserTerrain(session.World.Terrain, session.Vision);
        _entities.CaptureStep(session.World, session.LocalSlot);
    }

    public BrowserTerrain Terrain { get; }
    public RenderView View { get; private set; }

    /// <summary>The last frame, laid out as the remarks describe. Valid until the next <see cref="Render"/>.</summary>
    public ArraySegment<byte> Packet => new(_packet, 0, _packetLength);

    public ReadOnlySpan<byte> Uniforms => _packet.AsSpan(_header[0], _header[1]);
    public ReadOnlySpan<byte> Vertices => _packet.AsSpan(_header[2], _header[3]);

    /// <param name="silent">A tick from the past, replayed to catch up: a hit in it does not shake the view.</param>
    public void CaptureStep(bool silent)
    {
        _entities.CaptureStep(_session.World, _session.LocalSlot);
        if (!silent)
            _screenEffects.AfterStep(_session.World.StepEvents, _session.Player);
        Terrain.ObserveStep(_visualTime);
    }

    public void Render(int width, int height, int scale, float alpha, float delta, float time, float powerLabelCellSize = 0f)
    {
        _visualTime = time;
        width = Math.Clamp(width, 1, 3840);
        height = Math.Clamp(height, 1, 2160);
        scale = Math.Max(Math.Clamp(scale, 1, 32), Math.Max((width + 619) / 620, (height + 379) / 380));
        alpha = Math.Clamp(alpha, 0f, 1f);
        var positions = _entities.InterpolateTankPositions(alpha);
        PositionF center = positions.Length > _session.LocalSlot ? positions[_session.LocalSlot] : (PositionF)_session.Player.Position;
        var camera = new Position((int)MathF.Round(center.X * scale) - width / 2,
            (int)MathF.Round(center.Y * scale) - height / 2);
        View = new RenderView(new Size(width, height), _session.World.Terrain.Size, camera, scale);
        Terrain.Prepare(View.VisibleCells(), time);
        _entities.Build(View, alpha, Math.Clamp(delta, 0f, 0.1f), snapHeadings: false, time,
            _session.Vision, _session.Radar, powerLabelCellSize);
        int glowCount = _glowBuilder.BuildGpuTankHeatGlowData(_session.World.TankList.Tanks, positions, View, _glows, _session.Vision);
        var upload = new GamePixelsUpload(View,
            new PostProcessUploadOptions(_session.ThermalView, PostProcessPassFlags.All & ~PostProcessPassFlags.ThermalRegions, default, time),
            new TankGlowUpload(_glows, glowCount),
            new TerrainAuxUpload(Terrain.Texture(BrowserTerrain.Aux).Data, null, Terrain.Texture(BrowserTerrain.Coverage).Data, null),
            new NativeContinuousUpload(null, false, DesktopScreenTweaks.NativeContinuousSampleHigh), default,
            new FogUpload(Terrain.Texture(BrowserTerrain.Fog).Data, null),
            _screenEffects.Update(_session.Player, delta));
        PostParamsBlock block = PostParamsBlock.Build(upload, time);
        Pack(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref block, 1)),
            MemoryMarshal.AsBytes(_entities.Vertices.AsSpan(0, _entities.VertexCount)));
    }

    private void Pack(ReadOnlySpan<byte> uniforms, ReadOnlySpan<byte> vertices)
    {
        int width = _session.World.Terrain.Width;
        int length = HeaderInts * sizeof(int);
        _header[0] = length;
        _header[1] = uniforms.Length;
        length += uniforms.Length;
        _header[2] = length;
        _header[3] = vertices.Length;
        length += vertices.Length;
        for (int t = 0; t < BrowserTerrain.TextureCount; t++)
        {
            Rect rect = Terrain.Texture(t).Dirty ?? default;
            int at = 4 + 5 * t;
            _header[at] = length;
            _header[at + 1] = rect.X;
            _header[at + 2] = rect.Y;
            _header[at + 3] = rect.Width;
            _header[at + 4] = rect.Height;
            length += rect.Width * rect.Height * 4;
        }

        if (length > _packet.Length || (_packet.Length > MinPacketBytes && length < _packet.Length / ShrinkFactor))
            _packet = new byte[Math.Max(MinPacketBytes, length)];
        _packetLength = length;

        MemoryMarshal.AsBytes(_header.AsSpan()).CopyTo(_packet);
        uniforms.CopyTo(_packet.AsSpan(_header[0]));
        vertices.CopyTo(_packet.AsSpan(_header[2]));
        for (int t = 0; t < BrowserTerrain.TextureCount; t++)
        {
            TerrainTexture texture = Terrain.Texture(t);
            if (texture.Dirty is not Rect rect)
                continue;
            int rowBytes = rect.Width * 4;
            int target = _header[4 + 5 * t];
            for (int y = rect.Top; y < rect.Bottom; y++, target += rowBytes)
                Buffer.BlockCopy(texture.Data, (y * width + rect.X) * 4, _packet, target, rowBytes);
        }
    }
}
