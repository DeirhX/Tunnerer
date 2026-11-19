namespace Emberward.Browser.Client;

using Emberward.Core.Sensing;
using Emberward.Core.Terrain;
using Emberward.Core.Types;
using Emberward.Desktop.Rendering;

/// <summary>One of the renderer's world-sized textures: its CPU copy and the part a frame must upload (null: nothing).</summary>
public readonly record struct TerrainTexture(byte[] Data, Rect? Dirty);

/// <summary>
/// The four world-sized textures WebGL draws the terrain from: the ground's colour, the desktop renderer's aux and
/// material coverage fields, and the fog. The desktop's own <see cref="TerrainAuxUploader"/> and
/// <see cref="FogLayer"/> decide what a frame repacks, so a frame costs what changed in view; this class adds the
/// colour texture, which desktop keeps in a format WebGL cannot take.
/// </summary>
public sealed class BrowserTerrain
{
    public const int Color = 0, Aux = 1, Coverage = 2, Fog = 3, TextureCount = 4;

    private readonly TerrainGrid _grid;
    private readonly VisionMap _vision;
    private readonly TerrainAuxUploader _aux;
    private readonly FogLayer _fog;
    private readonly byte[] _color;
    private readonly TerrainTexture[] _textures = new TerrainTexture[TextureCount];
    private Rect? _colorDirty;
    private bool _stepped;

    public BrowserTerrain(TerrainGrid grid, VisionMap vision)
    {
        _grid = grid;
        _vision = vision;
        _aux = new TerrainAuxUploader(grid.Size, new TerrainAuxBuilder(), new Emberward.Desktop.DrawProfile());
        _fog = new FogLayer(grid.Size);
        _color = new byte[grid.Size.Area * 4];
        for (int cell = 0; cell < grid.Size.Area; cell++)
            WriteColor(cell);
        _colorDirty = new Rect(0, 0, grid.Width, grid.Height);
    }

    /// <summary>What the last <see cref="Prepare"/> left to upload of texture <paramref name="index"/>.</summary>
    public TerrainTexture Texture(int index) => _textures[index];

    /// <summary>Takes in what a simulation step changed of the ground the player knows.</summary>
    public void ObserveStep(float time)
    {
        _stepped = true;
        IReadOnlyList<Position> changes = _vision.KnownChanges;
        _aux.ObserveTerrainChanges(_vision.Known, changes, time);
        for (int i = 0; i < changes.Count; i++)
        {
            Position cell = changes[i];
            WriteColor(cell.X + cell.Y * _grid.Width);
            _colorDirty = RectMath.Union(_colorDirty, new Rect(cell.X, cell.Y, 1, 1));
        }
    }

    /// <summary>Repacks what changed since the last frame; <paramref name="visible"/> is the cells in view.</summary>
    public void Prepare(in Rect visible, float time)
    {
        FogUpload fog = _fog.Prepare(_vision, out Rect? sightChanged);
        TerrainAuxFrame frame = _aux.Prepare(
            TerrainView.Fogged(_grid, _vision), visible, _stepped, time, sightChanged);
        _aux.Commit(_grid, frame);
        _stepped = false;

        _textures[Color] = new TerrainTexture(_color, _colorDirty);
        _textures[Aux] = new TerrainTexture(frame.Upload.Data!, frame.Upload.DirtyRect);
        _textures[Coverage] = new TerrainTexture(frame.Upload.MaterialCoverage!, frame.Upload.MaterialCoverageDirtyRect);
        _textures[Fog] = new TerrainTexture(fog.Data!, fog.DirtyRect);
        _colorDirty = null;
    }

    private void WriteColor(int cell)
    {
        var color = Pixel.GetColor(_vision.Known.GetPixelRaw(cell));
        int p = cell * 4;
        _color[p] = color.R;
        _color[p + 1] = color.G;
        _color[p + 2] = color.B;
        _color[p + 3] = 255;
    }
}
