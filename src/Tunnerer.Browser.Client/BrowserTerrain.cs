namespace Tunnerer.Browser.Client;

using Tunnerer.Core.Sensing;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;
using Tunnerer.Desktop.Rendering;

/// <summary>The desktop renderer's terrain fields, packed into bounded uploads for WebGL.</summary>
public sealed class BrowserTerrain
{
    private readonly TerrainGrid _grid;
    private readonly VisionMap _vision;
    private readonly TerrainAuxBuilder _builder = new();
    private readonly byte[] _aux, _coverage;
    private readonly byte[][] _uploads = [[], [], [], []];
    private readonly List<Position> _expired = new();
    private Rect _covered;
    private bool _dirty = true;

    public BrowserTerrain(TerrainGrid grid, VisionMap vision)
    {
        _grid = grid;
        _vision = vision;
        _aux = new byte[grid.Size.Area * 4];
        _coverage = new byte[grid.Size.Area * 4];
        _builder.GrowthFade.Reset(grid.Size);
        // Match desktop's initial live-channel snapshot. Zero is deep cold in the signed heat encoding;
        // leaving unseen neighbours at zero would add a false frost rim wherever fog is softened.
        // After this one startup copy, only cells in sight refresh their live channels.
        for (int cell = 0; cell < grid.Size.Area; cell++)
        {
            _aux[cell * 4] = TerrainGrid.HeatToByte(grid.GetHeatTemperature(cell));
            _aux[cell * 4 + 3] = grid.GetScorchIntensity(cell);
        }
    }

    public Rect UploadRect { get; private set; }
    public byte[] Texture(int index) => _uploads[index];

    public void ObserveStep(float time)
    {
        _dirty = true;
        _builder.GrowthFade.ObserveChanges(_vision.Known, _vision.KnownChanges, time);
    }

    public void Prepare(Rect view, float time)
    {
        _expired.Clear();
        _builder.GrowthFade.CollectExpired(time, _expired);
        if (_expired.Count > 0)
            _dirty = true;
        if (!_dirty && (_covered.Left == 0 || view.Left >= _covered.Left + 4) &&
            (_covered.Right == _grid.Width || view.Right <= _covered.Right - 4) &&
            (_covered.Top == 0 || view.Top >= _covered.Top + 4) &&
            (_covered.Bottom == _grid.Height || view.Bottom <= _covered.Bottom - 4))
        {
            UploadRect = default;
            return;
        }
        int x0 = Math.Max(0, view.Left - 12), y0 = Math.Max(0, view.Top - 12);
        int x1 = Math.Min(_grid.Width, view.Right + 12), y1 = Math.Min(_grid.Height, view.Bottom + 12);
        if (x1 <= x0 || y1 <= y0)
        {
            UploadRect = default;
            return;
        }
        var requested = new Rect(x0, y0, x1 - x0, y1 - y0);
        UploadRect = _builder.BuildGpuTerrainAuxRect(TerrainView.Fogged(_grid, _vision), _aux, _coverage, requested);
        _covered = UploadRect;
        _dirty = false;
        int bytes = UploadRect.Width * UploadRect.Height * 4;
        for (int i = 0; i < _uploads.Length; i++)
            if (_uploads[i].Length != bytes)
                _uploads[i] = new byte[bytes];
        for (int y = 0; y < UploadRect.Height; y++)
        {
            int row = (y + UploadRect.Y) * _grid.Width + UploadRect.X;
            int target = y * UploadRect.Width * 4;
            Buffer.BlockCopy(_aux, row * 4, _uploads[1], target, UploadRect.Width * 4);
            Buffer.BlockCopy(_coverage, row * 4, _uploads[2], target, UploadRect.Width * 4);
            for (int x = 0; x < UploadRect.Width; x++)
            {
                int cell = row + x, p = target + x * 4;
                var color = Pixel.GetColor(_vision.Known.GetPixelRaw(cell));
                _uploads[0][p] = color.R;
                _uploads[0][p + 1] = color.G;
                _uploads[0][p + 2] = color.B;
                _uploads[0][p + 3] = 255;
                _uploads[3][p] = _vision.LevelAt(cell);
                _uploads[3][p + 1] = _vision.IsExplored(cell) ? (byte)255 : (byte)0;
            }
        }
    }
}
