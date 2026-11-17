namespace Tunnerer.Browser.Client;

using System.Runtime.InteropServices;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Config;
using Tunnerer.Desktop.Rendering;

/// <summary>Runs the shared desktop presentation pipeline; the browser only uploads its bytes and draws.</summary>
public sealed class BrowserScene
{
    private readonly BrowserSession _session;
    private readonly EntityLayerBuilder _entities = new();
    private readonly TerrainAuxBuilder _glowBuilder = new();
    private readonly float[] _glows = new float[PostParamsBlock.MaxTankGlows * 4];
    private float _visualTime;

    public BrowserScene(BrowserSession session)
    {
        _session = session;
        Terrain = new BrowserTerrain(session.World.Terrain, session.Vision);
        _entities.CaptureStep(session.World, 0);
    }

    public BrowserTerrain Terrain { get; }
    public RenderView View { get; private set; }
    public byte[] Uniforms { get; private set; } = [];
    public byte[] Vertices { get; private set; } = [];
    public Rect CellView { get; private set; }

    public void CaptureStep()
    {
        _entities.CaptureStep(_session.World, 0);
        Terrain.ObserveStep(_visualTime);
    }

    public void Render(int width, int height, int scale, float alpha, float delta, float time)
    {
        _visualTime = time;
        width = Math.Clamp(width, 1, 3840);
        height = Math.Clamp(height, 1, 2160);
        scale = Math.Max(Math.Clamp(scale, 1, 32), Math.Max((width + 619) / 620, (height + 379) / 380));
        alpha = Math.Clamp(alpha, 0f, 1f);
        var positions = _entities.InterpolateTankPositions(alpha);
        PositionF center = positions.Length > 0 ? positions[0] : (PositionF)_session.Player.Position;
        var camera = new Position((int)MathF.Round(center.X * scale) - width / 2,
            (int)MathF.Round(center.Y * scale) - height / 2);
        View = new RenderView(new Size(width, height), _session.World.Terrain.Size, camera, scale);
        CellView = new Rect((int)MathF.Floor(camera.X / (float)scale), (int)MathF.Floor(camera.Y / (float)scale),
            (width + scale - 1) / scale + 1, (height + scale - 1) / scale + 1);
        Terrain.Prepare(CellView, time);
        _entities.Build(View, alpha, Math.Clamp(delta, 0f, 0.1f), snapHeadings: false, time, _session.Vision, _session.Radar);
        Vertices = MemoryMarshal.AsBytes(_entities.Vertices.AsSpan(0, _entities.VertexCount)).ToArray();
        int glowCount = _glowBuilder.BuildGpuTankHeatGlowData(_session.World.TankList.Tanks, positions, View, _glows, _session.Vision);
        var upload = new GamePixelsUpload(View,
            new PostProcessUploadOptions(false, PostProcessPassFlags.All & ~PostProcessPassFlags.ThermalRegions, default, time),
            new TankGlowUpload(_glows, glowCount), new TerrainAuxUpload(Terrain.Texture(1), null, Terrain.Texture(2), null),
            new NativeContinuousUpload(null, false, 4), default, new FogUpload(Terrain.Texture(3), null));
        var block = PostParamsBlock.Build(upload, time);
        Uniforms = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref block, 1)).ToArray();
    }
}
