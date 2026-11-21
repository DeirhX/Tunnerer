namespace Emberward.Core.Terrain;

using Emberward.Core.Types;

public enum TerrainPixel : byte
{
    LevelGenDirt = 0,
    LevelGenRock = 1,
    LevelGenMark = 2,

    Blank = (byte)' ',
    Dirt = (byte)'D',
    DirtGrow = (byte)'g',
    Rock = (byte)'r',
    Decal = (byte)'.',
    BaseMin = (byte)'0',
    BaseMax = (byte)'7',
    BaseBarrier = (byte)'8',
    BaseFloor = (byte)'9',
    BaseCore = (byte)':',
    // A gate whose shield is spent (DESIGN.md §4.9): the same open ground as BaseBarrier, drawn open. The gate
    // shield (BasePowerSystem) owns which of the two a gate cell is; nothing reads the difference but the eye.
    BaseGateOpen = (byte)';',
    HotRock = (byte)'h',
    FrostRock = (byte)'f',
    Concrete = (byte)'C',
    // Emberite ore (DESIGN.md §6.3): the resource machines are made of. Ore is stone that is seeded with heat (Tweaks.Ore),
    // finite and cooling like any ground; a core cell sits at the heart of a lump and starts far hotter.
    Emberite = (byte)'o',
    EmberiteCore = (byte)'O',
}

public enum ThermalMaterial : byte
{
    Air = 0,
    Dirt = 1,
    Stone = 2,
    Base = 3,
    ConstantEnergy = 4,
    HotRock = 5,
    FrostRock = 6,
    EmberiteCore = 7,
}

/// <summary>
/// Single source of truth for terrain classification.
/// Adding a new terrain pixel = one entry in <see cref="Pixel.BuildBehaviorTable"/>.
/// All <c>Pixel.Is*</c> methods delegate to this table.
/// </summary>
public readonly record struct TerrainBehavior(
    bool BlocksMovement,
    bool SoftCollision,
    bool Diggable,
    bool Concrete,
    bool Mineral,
    bool Base,
    bool Scorched,
    Color DisplayColor,
    bool Ore = false)
{
    public bool IsAnyCollision => BlocksMovement || SoftCollision;
    public bool Torchable => Diggable || Mineral;
    public bool Rock => Mineral && !Concrete;
    public bool Dirt => SoftCollision;
}

public static class Pixel
{
    private static readonly TerrainBehavior[] _behaviors = BuildBehaviorTable();
    private static readonly ThermalMaterial[] _thermalMaterials = BuildThermalMaterialTable();

    private static readonly Color MagentaFallback = new(0xff, 0x00, 0xff);

    private static TerrainBehavior[] BuildBehaviorTable()
    {
        var table = new TerrainBehavior[256];

        void Set(TerrainPixel p, TerrainBehavior b) => table[(int)p] = b;

        Set(TerrainPixel.Blank, new(
            BlocksMovement: false, SoftCollision: false, Diggable: false,
            Concrete: false, Mineral: false, Base: false, Scorched: false,
            DisplayColor: new Color(0x00, 0x00, 0x00)));
        Set(TerrainPixel.Dirt, new(
            BlocksMovement: false, SoftCollision: true, Diggable: true,
            Concrete: false, Mineral: false, Base: false, Scorched: false,
            DisplayColor: new Color(0xc3, 0x79, 0x30)));
        Set(TerrainPixel.DirtGrow, new(
            BlocksMovement: false, SoftCollision: false, Diggable: true,
            Concrete: false, Mineral: false, Base: false, Scorched: false,
            DisplayColor: new Color(0x6a, 0x29, 0x02)));
        Set(TerrainPixel.Rock, new(
            BlocksMovement: true, SoftCollision: false, Diggable: false,
            Concrete: false, Mineral: true, Base: false, Scorched: false,
            DisplayColor: new Color(0x9a, 0x9a, 0x9a)));
        Set(TerrainPixel.Decal, new(
            BlocksMovement: false, SoftCollision: false, Diggable: false,
            Concrete: false, Mineral: false, Base: false, Scorched: true,
            DisplayColor: new Color(0x38, 0x30, 0x28)));
        Set(TerrainPixel.Concrete, new(
            BlocksMovement: true, SoftCollision: false, Diggable: false,
            Concrete: true, Mineral: true, Base: false, Scorched: false,
            DisplayColor: new Color(0x90, 0x90, 0x95)));
        Set(TerrainPixel.BaseBarrier, new(
            BlocksMovement: false, SoftCollision: false, Diggable: false,
            Concrete: false, Mineral: false, Base: false, Scorched: false,
            DisplayColor: new Color(0x50, 0x54, 0x5C)));
        Set(TerrainPixel.BaseGateOpen, new(
            BlocksMovement: false, SoftCollision: false, Diggable: false,
            Concrete: false, Mineral: false, Base: false, Scorched: false,
            DisplayColor: new Color(0x30, 0x2C, 0x28)));
        Set(TerrainPixel.HotRock, new(
            BlocksMovement: true, SoftCollision: false, Diggable: false,
            Concrete: false, Mineral: false, Base: false, Scorched: false,
            DisplayColor: new Color(0xd0, 0x40, 0x10)));
        Set(TerrainPixel.FrostRock, new(
            BlocksMovement: true, SoftCollision: false, Diggable: false,
            Concrete: false, Mineral: false, Base: false, Scorched: false,
            DisplayColor: new Color(0x9c, 0xd8, 0xf0)));
        // Ore blocks movement but is neither diggable nor mineral: the tank's dig has its own rule for it
        // (TankDigger), shots do not break it and it never regrows.
        Set(TerrainPixel.Emberite, new(
            BlocksMovement: true, SoftCollision: false, Diggable: false,
            Concrete: false, Mineral: false, Base: false, Scorched: false,
            DisplayColor: new Color(0x3c, 0xb8, 0xb0), Ore: true));
        Set(TerrainPixel.EmberiteCore, new(
            BlocksMovement: true, SoftCollision: false, Diggable: false,
            Concrete: false, Mineral: false, Base: false, Scorched: false,
            DisplayColor: new Color(0xb0, 0x5c, 0xd8), Ore: true));
        Set(TerrainPixel.BaseFloor, new(
            BlocksMovement: false, SoftCollision: false, Diggable: false,
            Concrete: false, Mineral: false, Base: false, Scorched: false,
            DisplayColor: new Color(0x3A, 0x3E, 0x46)));
        // The core is the one part of a base that takes damage (DESIGN.md §4.9): it stops tanks and shells like a
        // wall, and an enemy shell that reaches it wears the base's armor down.
        Set(TerrainPixel.BaseCore, new(
            BlocksMovement: true, SoftCollision: false, Diggable: false,
            Concrete: false, Mineral: false, Base: false, Scorched: false,
            DisplayColor: new Color(0xE0, 0xA0, 0x40)));

        var baseColor = new Color(0x58, 0x5C, 0x64);
        for (byte b = (byte)TerrainPixel.BaseMin; b <= (byte)TerrainPixel.BaseMax; b++)
            table[b] = new(
                BlocksMovement: true, SoftCollision: false, Diggable: false,
                Concrete: false, Mineral: false, Base: true, Scorched: false,
                DisplayColor: baseColor);

        return table;
    }

    public static ref readonly TerrainBehavior GetBehavior(TerrainPixel p) => ref _behaviors[(int)p];

    public static bool IsDirt(TerrainPixel p) => _behaviors[(int)p].Dirt;
    public static bool IsDiggable(TerrainPixel p) => _behaviors[(int)p].Diggable;
    public static bool IsTorchable(TerrainPixel p) => _behaviors[(int)p].Torchable;
    public static bool IsSoftCollision(TerrainPixel p) => _behaviors[(int)p].SoftCollision;
    public static bool IsBlockingCollision(TerrainPixel p) => _behaviors[(int)p].BlocksMovement;
    public static bool IsAnyCollision(TerrainPixel p) => _behaviors[(int)p].IsAnyCollision;
    public static bool IsBase(TerrainPixel p) => _behaviors[(int)p].Base;

    /// <summary>
    /// True when the pixel blocks a link's line of sight: links need direct visibility, so anything but open ground
    /// (dug tunnel or scorch) blocks them, dirt included. Base cells do not, so a relay can link to its
    /// base from any side rather than only through a gate.
    /// </summary>
    public static bool BlocksLink(TerrainPixel p) =>
        !IsOpenGround(p) && !IsAnyBaseCell(p);

    /// <summary>
    /// True when the pixel stops a creature's sight and senses: plain, hot and frost rock, and concrete. Dirt, open
    /// ground, ore and base cells do not, so creatures sense through soil but not through stone (unlike
    /// <see cref="BlocksLink"/>, which dirt blocks too). <see cref="IsRock"/> alone is plain rock only, hence the list.
    /// </summary>
    public static bool BlocksCreatureSight(TerrainPixel p) =>
        p is TerrainPixel.Rock or TerrainPixel.HotRock or TerrainPixel.FrostRock or TerrainPixel.Concrete;
    public static bool IsScorched(TerrainPixel p) => _behaviors[(int)p].Scorched;
    public static bool IsConcrete(TerrainPixel p) => _behaviors[(int)p].Concrete;
    public static bool IsRock(TerrainPixel p) => _behaviors[(int)p].Rock;
    public static bool IsMineral(TerrainPixel p) => _behaviors[(int)p].Mineral;

    /// <summary>Ground with nothing in it: dug tunnel or scorch.</summary>
    public static bool IsOpenGround(TerrainPixel p) => p == TerrainPixel.Blank || IsScorched(p);

    /// <summary>Dirt, grown or still growing back.</summary>
    public static bool IsSoil(TerrainPixel p) => IsDirt(p) || p == TerrainPixel.DirtGrow;

    /// <summary>Soil and plain rock: the ground level generation may turn into pockets and deposits.</summary>
    public static bool IsSolidGround(TerrainPixel p) => IsSoil(p) || IsRock(p);

    /// <summary>Any cell of a base: walls and baffles, floor, gates and the core.</summary>
    public static bool IsAnyBaseCell(TerrainPixel p) =>
        IsBase(p) || p is TerrainPixel.BaseBarrier or TerrainPixel.BaseGateOpen or TerrainPixel.BaseFloor or TerrainPixel.BaseCore;

    /// <summary>
    /// A gate cell of a base, shown shut or open: open ground to its owner, shut to the enemy while the gate shield
    /// holds (ask <see cref="World.AreGatesShut"/>, not the pixel).
    /// </summary>
    public static bool IsBaseGate(TerrainPixel p) => p is TerrainPixel.BaseBarrier or TerrainPixel.BaseGateOpen;

    /// <summary>A cell of a base's core, the only part of a base that takes damage.</summary>
    public static bool IsBaseCore(TerrainPixel p) => p == TerrainPixel.BaseCore;

    /// <summary>True for emberite ore, plain or core.</summary>
    public static bool IsOre(TerrainPixel p) => _behaviors[(int)p].Ore;

    /// <summary>True for the warm heart of a lump, which needs the torch and is worth more than a plain cell.</summary>
    public static bool IsOreCore(TerrainPixel p) => p == TerrainPixel.EmberiteCore;

    /// <summary>Ore units a mined cell yields: 0 for anything that is not ore.</summary>
    public static int OreUnits(TerrainPixel p) => p switch
    {
        TerrainPixel.Emberite => Config.Tweaks.Ore.PlainCellUnits,
        TerrainPixel.EmberiteCore => Config.Tweaks.Ore.CoreCellUnits,
        _ => 0,
    };

    /// <summary>Hot path of the thermal solver: a plain array lookup instead of re-deriving the material.</summary>
    public static ThermalMaterial GetThermalMaterial(TerrainPixel p) => _thermalMaterials[(int)p];

    private static ThermalMaterial[] BuildThermalMaterialTable()
    {
        var table = new ThermalMaterial[256];
        for (int i = 0; i < table.Length; i++)
            table[i] = DeriveThermalMaterial((TerrainPixel)i);
        return table;
    }

    private static ThermalMaterial DeriveThermalMaterial(TerrainPixel p)
    {
        var b = _behaviors[(int)p];
        return p switch
        {
            TerrainPixel.HotRock => ThermalMaterial.HotRock,
            TerrainPixel.FrostRock => ThermalMaterial.FrostRock,
            TerrainPixel.EmberiteCore => ThermalMaterial.EmberiteCore,
            TerrainPixel.BaseFloor => ThermalMaterial.ConstantEnergy,
            TerrainPixel.BaseBarrier or TerrainPixel.BaseGateOpen or TerrainPixel.BaseCore => ThermalMaterial.Base,
            _ when b.Base => ThermalMaterial.Base,
            _ when b.SoftCollision => ThermalMaterial.Dirt,
            _ when b.BlocksMovement => ThermalMaterial.Stone,
            _ => ThermalMaterial.Air,
        };
    }

    public static Color GetColor(TerrainPixel p)
    {
        var c = _behaviors[(int)p].DisplayColor;
        return c == default ? MagentaFallback : c;
    }
}
