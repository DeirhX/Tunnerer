using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

namespace Tunnerer.Tests;

public class TerrainRegrowthTests
{
    [Fact]
    public void ParallelRegrowth_MatchesSequential()
    {
        var sequential = Grow(parallel: false);
        var parallel = Grow(parallel: true);

        Assert.True(sequential.Changed > 0, "the scenario must actually regrow something");
        for (int i = 0; i < sequential.Terrain.Size.Area; i++)
        {
            Assert.Equal(sequential.Terrain[i], parallel.Terrain[i]);
            Assert.Equal(sequential.Terrain.GetScorchIntensity(i), parallel.Terrain.GetScorchIntensity(i));
        }
    }

    [Fact]
    public void SkippingUnmarkedTiles_GivesTheResultOfScanningEveryTile()
    {
        var skipping = Grow(parallel: false);
        var scanningAll = Grow(parallel: false, markAllEveryStep: true);

        for (int i = 0; i < skipping.Terrain.Size.Area; i++)
        {
            Assert.Equal(scanningAll.Terrain[i], skipping.Terrain[i]);
            Assert.Equal(scanningAll.Terrain.GetScorchIntensity(i), skipping.Terrain.GetScorchIntensity(i));
        }
    }

    [Fact]
    public void SolidGround_IsLeftAlone_UntilSomethingIsDugOrScorchedThere()
    {
        var terrain = new TerrainGrid(new Size(160, 100));
        terrain.Fill(TerrainPixel.Dirt);
        var regrowth = new TerrainRegrowth(parallel: false, enabled: true, simulationSeed: 77);
        RegrowthTiles tiles = terrain.RegrowthTiles;
        Assert.True(tiles.IsMarked(0, 0), "every tile starts as a maybe");

        for (int step = 0; step < 40; step++)
            regrowth.Advance(terrain, step, []);
        for (int ty = 0; ty < tiles.CountY; ty++)
            for (int tx = 0; tx < tiles.CountX; tx++)
                Assert.False(tiles.IsMarked(tx, ty), $"tile {tx},{ty} holds nothing to regrow");

        // Every way of writing a pixel marks the tile it lands in, and only that one.
        terrain.SetPixel(new Position(40, 40), TerrainPixel.Blank);
        terrain.SetPixelRaw(new Position(100, 10), TerrainPixel.Decal);
        terrain[70 + 70 * terrain.Width] = TerrainPixel.DirtGrow;
        terrain.SetPixel(new Position(130, 90), TerrainPixel.Rock);
        Assert.True(tiles.IsMarked(40 / RegrowthTiles.TileSize, 40 / RegrowthTiles.TileSize));
        Assert.True(tiles.IsMarked(100 / RegrowthTiles.TileSize, 10 / RegrowthTiles.TileSize));
        Assert.True(tiles.IsMarked(70 / RegrowthTiles.TileSize, 70 / RegrowthTiles.TileSize));
        Assert.False(tiles.IsMarked(130 / RegrowthTiles.TileSize, 90 / RegrowthTiles.TileSize));
        Assert.False(tiles.IsMarked(0, 0));
    }

    private static (TerrainGrid Terrain, int Changed) Grow(bool parallel, bool markAllEveryStep = false)
    {
        // A dirt field with blank holes and scorched patches, so every regrowth rule fires.
        var terrain = new TerrainGrid(new Size(160, 100));
        terrain.Fill(TerrainPixel.Dirt);
        for (int y = 10; y < 90; y += 20)
        {
            for (int x = 10; x < 150; x += 20)
            {
                for (int dy = 0; dy < 8; dy++)
                {
                    for (int dx = 0; dx < 8; dx++)
                    {
                        int offset = (x + dx) + (y + dy) * terrain.Width;
                        bool scorched = (x / 20 + y / 20) % 2 == 0;
                        terrain[offset] = scorched ? TerrainPixel.Decal : TerrainPixel.Blank;
                        if (scorched)
                            terrain.SetScorchIntensity(offset, 200);
                    }
                }
            }
        }
        var before = new TerrainPixel[terrain.Size.Area];
        for (int i = 0; i < before.Length; i++)
            before[i] = terrain[i];

        var regrowth = new TerrainRegrowth(parallel, enabled: true, simulationSeed: 77);
        for (int step = 0; step < 600; step++)
        {
            if (markAllEveryStep)
                terrain.RegrowthTiles.MarkAll();
            regrowth.Advance(terrain, step, []);
        }

        int changed = 0;
        for (int i = 0; i < before.Length; i++)
            if (terrain[i] != before[i])
                changed++;
        return (terrain, changed);
    }

    [Fact]
    public void NoDirtGrows_InsideABlocker_ButItDoesOutside()
    {
        var terrain = new TerrainGrid(new Size(120, 60));
        terrain.Fill(TerrainPixel.Dirt);
        // Two tunnels five cells wide, one inside the blocker's circle and one clear of it.
        for (int x = 10; x < 110; x++)
            for (int dy = -2; dy <= 2; dy++)
                terrain.SetPixelRaw(new Position(x, 30 + dy), TerrainPixel.Blank);
        RegrowthBlocker[] blockers = [new RegrowthBlocker(new Position(30, 30), 15)];

        var regrowth = new TerrainRegrowth(parallel: false, enabled: true, simulationSeed: 77);
        for (int step = 0; step < 6000; step++)
            regrowth.Advance(terrain, step, blockers);

        int Open(int minX, int maxX)
        {
            int open = 0;
            for (int x = minX; x <= maxX; x++)
                for (int dy = -2; dy <= 2; dy++)
                    if (terrain.GetPixelRaw(new Position(x, 30 + dy)) == TerrainPixel.Blank)
                        open++;
            return open;
        }

        Assert.Equal(21 * 5, Open(20, 40));
        Assert.True(Open(70, 90) < 21 * 5 / 2, $"the tunnel outside the blocker should mostly have closed, open={Open(70, 90)}");
    }
}
