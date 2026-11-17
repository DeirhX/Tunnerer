namespace Tunnerer.Desktop.Rendering;

using Tunnerer.Core.Sensing;
using Tunnerer.Core.Terrain;

/// <summary>
/// The terrain the renderer draws. Without fog of war that is the world itself. With it, the pixels are the ones
/// the player's side last saw (<see cref="VisionMap.Known"/>), and heat and scorch are only read for cells in
/// sight, so ground out of sight stays as it was remembered.
/// </summary>
/// <param name="Pixels">Where terrain pixels are read from.</param>
/// <param name="Grid">The world's grid: heat, scorch and the heat dirty rect.</param>
/// <param name="Vision">The player's vision, or null when everything is shown live.</param>
public readonly record struct TerrainView(ITerrainPixelReadOnly Pixels, TerrainGrid Grid, VisionMap? Vision)
{
    public static TerrainView Live(TerrainGrid grid) => new(grid, grid, null);

    public static TerrainView Fogged(TerrainGrid grid, VisionMap vision) => new(vision.Known, grid, vision);

    public int Width => Grid.Width;
    public int Height => Grid.Height;
}
