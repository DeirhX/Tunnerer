namespace Tunnerer.Core.Regrowth;

using Tunnerer.Core.Config;

/// <summary>
/// The appointments <see cref="TerrainRegrowth"/> keeps: at most one per cell, the tick at which that cell is next
/// looked at. Regrowth visits a cell only when its appointment comes, so a cell with none costs nothing.
/// <para>
/// Appointments are filed by tick in a wheel of <see cref="WheelTicks"/> slots, so none may lie further ahead than
/// that. Each cell's own record (kept per tile, and only for tiles that have appointments) says which slot holds its
/// appointment; giving a cell a new one leaves the old entry in the wheel, where it no longer matches the record and
/// is skipped when its tick comes.
/// </para>
/// </summary>
internal sealed class RegrowthSchedule
{
    /// <summary>Slots in the wheel: an appointment must be nearer than this many ticks.</summary>
    public const int WheelTicks = 1 << 15;

    private const int TileSize = Tweaks.World.RegrowthTileSize;
    private const int MaxSpareTiles = 64;
    private const int SmallNodeCapacity = 256;

    private readonly int _width;
    private readonly int _tilesX;

    // The wheel: per slot a linked list of entries, each naming a cell.
    private readonly int[] _firstEntry = new int[WheelTicks];
    private int[] _entryCell = new int[SmallNodeCapacity];
    private int[] _entryNext = new int[SmallNodeCapacity];
    private int _entriesUsed;
    private int _freeEntry = -1;
    private int _entriesFiled;

    // Per tile, each cell's slot + 1 (0 = no appointment). A tile with no appointments holds no array.
    private readonly ushort[]?[] _slotOfTile;
    private readonly ushort[] _countOfTile;
    private readonly Stack<ushort[]> _spareTiles = new();

    public RegrowthSchedule(int width, int height)
    {
        _width = width;
        _tilesX = (width + TileSize - 1) / TileSize;
        int tilesY = (height + TileSize - 1) / TileSize;
        _slotOfTile = new ushort[]?[_tilesX * tilesY];
        _countOfTile = new ushort[_tilesX * tilesY];
        Array.Fill(_firstEntry, -1);
    }

    /// <summary>Cells that have an appointment.</summary>
    public int Count { get; private set; }

    public bool Has(int x, int y)
    {
        ushort[]? slots = _slotOfTile[TileOf(x, y)];
        return slots != null && slots[InTile(x, y)] != 0;
    }

    /// <summary>Gives the cell an appointment at <paramref name="tick"/>, in place of any it had.</summary>
    public void Set(int x, int y, int tick)
    {
        int tile = TileOf(x, y);
        ushort[] slots = _slotOfTile[tile] ??= _spareTiles.Count > 0 ? _spareTiles.Pop() : new ushort[TileSize * TileSize];
        int at = InTile(x, y);
        int slot = tick & (WheelTicks - 1);
        if (slots[at] == slot + 1)
            return;
        if (slots[at] == 0)
        {
            _countOfTile[tile]++;
            Count++;
        }
        slots[at] = (ushort)(slot + 1);
        File(slot, x + y * _width);
    }

    /// <summary>Drops the cell's appointment, if it has one.</summary>
    public void Clear(int x, int y)
    {
        int tile = TileOf(x, y);
        ushort[]? slots = _slotOfTile[tile];
        int at = InTile(x, y);
        if (slots == null || slots[at] == 0)
            return;
        slots[at] = 0;
        Count--;
        if (--_countOfTile[tile] == 0)
        {
            _slotOfTile[tile] = null;
            if (_spareTiles.Count < MaxSpareTiles)
                _spareTiles.Push(slots);
        }
    }

    /// <summary>
    /// Adds to <paramref name="cells"/> the cells whose appointment is at <paramref name="tick"/> and drops those
    /// appointments. Must be called for every tick in turn, or appointments wait a whole turn of the wheel.
    /// </summary>
    public void TakeDue(int tick, List<int> cells)
    {
        int slot = tick & (WheelTicks - 1);
        int entry = _firstEntry[slot];
        _firstEntry[slot] = -1;
        while (entry >= 0)
        {
            int cell = _entryCell[entry];
            int next = _entryNext[entry];
            _entryNext[entry] = _freeEntry;
            _freeEntry = entry;
            _entriesFiled--;
            entry = next;

            int x = cell % _width, y = cell / _width;
            ushort[]? slots = _slotOfTile[TileOf(x, y)];
            if (slots != null && slots[InTile(x, y)] == slot + 1)
            {
                Clear(x, y);
                cells.Add(cell);
            }
        }

        // A burst (a long tunnel dug at once) grows the entry store; give it back once the wheel is empty again.
        if (_entriesFiled == 0 && _entryCell.Length > SmallNodeCapacity)
        {
            _entryCell = new int[SmallNodeCapacity];
            _entryNext = new int[SmallNodeCapacity];
            _entriesUsed = 0;
            _freeEntry = -1;
        }
    }

    private void File(int slot, int cell)
    {
        int entry;
        if (_freeEntry >= 0)
        {
            entry = _freeEntry;
            _freeEntry = _entryNext[entry];
        }
        else
        {
            if (_entriesUsed == _entryCell.Length)
            {
                Array.Resize(ref _entryCell, _entriesUsed * 2);
                Array.Resize(ref _entryNext, _entriesUsed * 2);
            }
            entry = _entriesUsed++;
        }
        _entryCell[entry] = cell;
        _entryNext[entry] = _firstEntry[slot];
        _firstEntry[slot] = entry;
        _entriesFiled++;
    }

    private int TileOf(int x, int y) => x / TileSize + y / TileSize * _tilesX;

    private static int InTile(int x, int y) => x % TileSize + y % TileSize * TileSize;
}

/// <summary>
/// How many regrowth passes a cell waits for something that has a fixed chance of happening on each pass. Drawing the
/// wait once gives the same odds as rolling on every pass, without visiting the cell on the passes in between.
/// </summary>
internal readonly struct PassWait
{
    // [k] = the chance of still waiting after k + 1 passes, as a share of 2^32. Whole-number arithmetic, so every
    // platform builds the same table.
    private readonly uint[] _stillWaiting;

    /// <param name="perMille">Chance per pass, in thousandths.</param>
    /// <param name="maxPasses">The longest wait; the few longer ones end here.</param>
    public PassWait(int perMille, int maxPasses)
    {
        _stillWaiting = new uint[maxPasses - 1];
        ulong share = 1UL << 32;
        for (int k = 0; k < _stillWaiting.Length; k++)
        {
            share = share * (ulong)(1000 - perMille) / 1000;
            _stillWaiting[k] = (uint)Math.Min(share, uint.MaxValue);
        }
    }

    /// <summary>The wait in passes, at least one, for a uniformly random <paramref name="roll"/>.</summary>
    public int Passes(uint roll)
    {
        // The table falls from entry to entry: the wait is one more than the number of entries above the roll.
        int low = 0, high = _stillWaiting.Length;
        while (low < high)
        {
            int middle = (low + high) >> 1;
            if (_stillWaiting[middle] > roll)
                low = middle + 1;
            else
                high = middle;
        }
        return low + 1;
    }
}
