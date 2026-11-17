using Tunnerer.Core.Sensing;
using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;
using Tunnerer.Desktop;
using Tunnerer.Desktop.Rendering;
using Tunnerer.Desktop.Rendering.Entities;

namespace Tunnerer.Tests;

/// <summary>
/// The renderer's side of fog of war: the fog texture follows the vision map, ground out of sight keeps the heat it
/// last showed, and entities out of sight are not drawn.
/// </summary>
public class FogRenderingTests
{
    private static readonly Size TerrainSize = new(128, 96);
    private static readonly Rect Viewport = new(0, 0, 128, 96);
    private static readonly VisionSource[] None = [];
    private static readonly Position Seen = new(22, 20);
    private static readonly Position Unseen = new(100, 70);

    private readonly TerrainGrid _terrain = new(TerrainSize);
    private readonly VisionMap _vision;

    public FogRenderingTests()
    {
        _terrain.Fill(TerrainPixel.Blank);
        _vision = new VisionMap(_terrain);
        LookFrom(new Position(20, 20));
    }

    private void LookFrom(Position eye) => _vision.Update(_terrain, None, [new VisionSource(eye, 10, 0)]);

    private static int Index(Position p) => p.X + p.Y * TerrainSize.X;

    [Fact]
    public void FogTexture_HoldsTheLevelAndTheExploredFlag_AndRepacksOnlyWhatChanged()
    {
        var fog = new FogLayer(TerrainSize);

        FogUpload first = fog.Prepare(_vision, out Rect? firstChange);
        Assert.Equal(new Rect(0, 0, TerrainSize.X, TerrainSize.Y), first.DirtyRect);
        Assert.Equal(new Rect(10, 10, 21, 21), firstChange);
        Assert.Equal(255, first.Data![Index(Seen) * 4]);
        Assert.Equal(255, first.Data[Index(Seen) * 4 + 1]);
        Assert.Equal(0, first.Data[Index(Unseen) * 4]);
        Assert.Equal(0, first.Data[Index(Unseen) * 4 + 1]);

        FogUpload still = fog.Prepare(_vision, out Rect? noChange);
        Assert.Null(still.DirtyRect);
        Assert.Null(noChange);

        // The eye moves away: the old spot is no longer seen but stays explored.
        LookFrom(new Position(100, 70));
        FogUpload moved = fog.Prepare(_vision, out _);
        Assert.NotNull(moved.DirtyRect);
        Assert.Equal(0, moved.Data![Index(Seen) * 4]);
        Assert.Equal(255, moved.Data[Index(Seen) * 4 + 1]);
        Assert.Equal(255, moved.Data[Index(Unseen) * 4]);
    }

    [Fact]
    public void GroundOutOfSight_KeepsTheHeatItLastShowed_UntilItIsSeenAgain()
    {
        var uploader = new TerrainAuxUploader(TerrainSize, new TerrainAuxBuilder(), new DrawProfile());
        var view = new RenderViewState();
        var fog = new FogLayer(TerrainSize);
        TerrainView fogged = TerrainView.Fogged(_terrain, _vision);

        byte[] Frame()
        {
            fog.Prepare(_vision, out Rect? sightChanged);
            // Every frame follows a step here; two frames, because heat is packed every other one.
            TerrainAuxFrame frame = default;
            for (int i = 0; i < 2; i++)
            {
                frame = uploader.Prepare(fogged, view, Viewport, steppedSinceRender: true, visualTime: 0f, sightChanged);
                uploader.Commit(_terrain, frame);
                sightChanged = null;
            }
            return frame.Upload.Data!;
        }

        byte[] aux = Frame();
        byte cold = aux[Index(Seen) * 4];
        Assert.Equal(cold, aux[Index(Unseen) * 4]);

        _terrain.AddHeat(Seen, 200);
        _terrain.AddHeat(Unseen, 200);
        aux = Frame();
        Assert.True(aux[Index(Seen) * 4] > cold, "heat in sight is shown");
        Assert.Equal(cold, aux[Index(Unseen) * 4]);

        LookFrom(new Position(100, 70));
        aux = Frame();
        Assert.True(aux[Index(Unseen) * 4] > cold, "coming into sight shows the heat that built up unseen");
    }

    [Fact]
    public void WithoutFog_HeatIsShownEverywhere()
    {
        var uploader = new TerrainAuxUploader(TerrainSize, new TerrainAuxBuilder(), new DrawProfile());
        var view = new RenderViewState();
        TerrainView live = TerrainView.Live(_terrain);
        TerrainAuxFrame frame = uploader.Prepare(live, view, Viewport, steppedSinceRender: true, visualTime: 0f);
        uploader.Commit(_terrain, frame);
        byte cold = frame.Upload.Data![Index(Unseen) * 4];

        _terrain.AddHeat(Unseen, 200);
        for (int i = 0; i < 2; i++)
        {
            frame = uploader.Prepare(live, view, Viewport, steppedSinceRender: true, visualTime: 0f);
            uploader.Commit(_terrain, frame);
        }

        Assert.True(frame.Upload.Data![Index(Unseen) * 4] > cold);
    }

    [Fact]
    public void Entities_AreDrawnOnlyInSight_AndAlwaysWithoutFog()
    {
        Assert.True(Sight.Sees(_vision, Seen));
        Assert.False(Sight.Sees(_vision, Unseen));
        Assert.True(Sight.Sees(_vision, Seen.X + 0.9f, Seen.Y + 0.9f));
        Assert.False(Sight.Sees(_vision, -3f, 5f));
        Assert.True(Sight.Sees(null, Unseen));
    }
}
