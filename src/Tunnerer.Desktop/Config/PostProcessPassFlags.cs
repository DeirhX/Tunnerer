namespace Tunnerer.Desktop.Config;

[Flags]
public enum PostProcessPassFlags : byte
{
    None = 0,
    Vignette = 1 << 0,
    TerrainAux = 1 << 2,
    TankGlow = 1 << 3,
    NativeTerrainSmoothing = 1 << 4,
    TerrainHeat = 1 << 5,
    NativeEdgeCurving = 1 << 6,
    ThermalRegions = 1 << 7,
    All = Vignette | TerrainAux | TankGlow | NativeTerrainSmoothing | TerrainHeat | NativeEdgeCurving | ThermalRegions,
}
