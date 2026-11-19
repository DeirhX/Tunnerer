namespace Emberward.Desktop.Rendering;

using Emberward.Core.Sensing;
using Emberward.Core.Types;

/// <summary>
/// The CPU copy of the fog texture, one texel per terrain cell: R = how well the cell is seen (0 out of sight,
/// 255 in plain sight), G = 255 once the cell has been explored. Each frame it repacks the rectangle in which the
/// player's <see cref="VisionMap"/> changed, so a tank standing still uploads nothing.
/// </summary>
public sealed class FogLayer
{
    private readonly byte[] _data;
    private bool _fullUploadPending = true;

    public FogLayer(Size terrainSize)
    {
        _data = new byte[terrainSize.Area * 4];
    }

    /// <summary>
    /// Packs what changed since the last call. <paramref name="sightChanged"/> is the rectangle of cells whose
    /// visibility changed (null when none did); the terrain aux data must refresh the live channels there.
    /// </summary>
    public FogUpload Prepare(VisionMap vision, out Rect? sightChanged)
    {
        sightChanged = vision.TryGetDirtyRect(out Rect dirty) ? dirty : null;
        vision.ClearDirtyRect();

        Rect? packed = _fullUploadPending ? new Rect(0, 0, vision.Width, vision.Height) : sightChanged;
        _fullUploadPending = false;
        if (packed is Rect rect)
            Pack(vision, rect);
        return new FogUpload(_data, packed);
    }

    private void Pack(VisionMap vision, in Rect rect)
    {
        RectMath.GetMinMaxInclusive(rect, out int minX, out int minY, out int maxX, out int maxY);
        int w = vision.Width;
        for (int y = minY; y <= maxY; y++)
        {
            int row = y * w;
            for (int x = minX; x <= maxX; x++)
            {
                int idx = row + x;
                _data[idx * 4] = vision.LevelAt(idx);
                _data[idx * 4 + 1] = vision.IsExplored(idx) ? (byte)255 : (byte)0;
            }
        }
    }
}
