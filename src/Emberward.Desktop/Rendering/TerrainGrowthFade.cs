namespace Emberward.Desktop.Rendering;

using Emberward.Core.Terrain;
using Emberward.Core.Types;

/// <summary>
/// Remembers when each cell regrew into dirt, so the shader can grow it out of the cave edge over
/// <see cref="FadeSeconds"/> instead of the edge jumping a whole cell at once. Render-only: the simulation still
/// turns the cell solid immediately.
/// </summary>
/// <remarks>
/// The stamp is a byte in the coverage texture's alpha: 0 = settled, otherwise 1 + the regrow time in
/// 1/<see cref="TicksPerSecond"/> s ticks, modulo 255. Must match the constants in Common/TerrainEdge.hlsli.
/// The byte wraps every ~8 s, so each stamp is cleared (and its cell re-uploaded) once the fade is over.
/// </remarks>
public sealed class TerrainGrowthFade
{
    public const float TicksPerSecond = 32f;
    public const float FadeSeconds = 0.5f;
    private const float ClearAfterSeconds = FadeSeconds + 0.15f;

    private byte[] _stamps = Array.Empty<byte>();
    private readonly Queue<(int Index, float ClearAt)> _pending = new();
    private int _width;

    public void Reset(Size size)
    {
        _width = size.X;
        _stamps = new byte[size.X * size.Y];
        _pending.Clear();
    }

    public byte StampAt(int index) => (uint)index < (uint)_stamps.Length ? _stamps[index] : (byte)0;

    /// <summary>Stamps cells that just regrew; clears the stamp of cells that were dug out again.</summary>
    public void ObserveChanges(ITerrainPixelReadOnly terrain, IReadOnlyList<Position> changedCells, float timeSeconds)
    {
        if (_stamps.Length != terrain.Width * terrain.Height)
            Reset(new Size(terrain.Width, terrain.Height));

        byte stamp = (byte)(1 + (long)MathF.Floor(timeSeconds * TicksPerSecond) % 255);
        for (int i = 0; i < changedCells.Count; i++)
        {
            Position p = changedCells[i];
            int index = p.X + p.Y * _width;
            if ((uint)index >= (uint)_stamps.Length) continue;
            TerrainPixel pixel = terrain.GetPixelRaw(index);
            if (pixel == TerrainPixel.DirtGrow)
            {
                if (_stamps[index] != 0) continue;
                _stamps[index] = stamp;
                _pending.Enqueue((index, timeSeconds + ClearAfterSeconds));
            }
            else if (!TerrainSolidField.IsSolidTerrain(pixel))
            {
                _stamps[index] = 0;
            }
        }
    }

    /// <summary>Clears stamps whose fade has finished and adds their cells to <paramref name="dirtyCells"/>.</summary>
    public void CollectExpired(float timeSeconds, List<Position> dirtyCells)
    {
        while (_pending.Count > 0 && _pending.Peek().ClearAt <= timeSeconds)
        {
            int index = _pending.Dequeue().Index;
            if (_stamps[index] == 0) continue;
            _stamps[index] = 0;
            dirtyCells.Add(new Position(index % _width, index / _width));
        }
    }
}
