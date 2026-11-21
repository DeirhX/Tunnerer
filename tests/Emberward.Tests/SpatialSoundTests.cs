namespace Emberward.Tests;

using Emberward.Core;
using Emberward.Core.Collision;
using Emberward.Core.Input;
using Emberward.Core.Match;
using Emberward.Core.Terrain;
using Emberward.Core.Types;
using Emberward.Desktop.Audio;
using Emberward.Desktop.Config;

/// <summary>
/// Where sounds are heard from (<see cref="SoundSpace"/>, <see cref="SoundDirector"/>): distance, direction, ground in
/// the way, and another tank coming closer.
/// </summary>
public class SpatialSoundTests
{
    private sealed class RecordingSink : ISoundSink
    {
        public readonly List<(SoundEffect Effect, float Gain, float Pan, float Rate, float Muffle)> Loops = new();

        public void Play(SoundEffect effect, float gain, float pan, float rate, float muffle = 0f) { }

        public int AddLoop(SoundEffect effect)
        {
            Loops.Add((effect, 0f, 0f, 1f, 0f));
            return Loops.Count - 1;
        }

        public void SetLoop(int handle, float gain, float pan = 0f, float rate = 1f, float muffle = 0f) =>
            Loops[handle] = (Loops[handle].Effect, gain, pan, rate, muffle);

        public (SoundEffect Effect, float Gain, float Pan, float Rate, float Muffle) Loop(int tank, SoundEffect effect) =>
            Loops.Where(l => l.Effect == effect).ElementAt(tank);
    }

    private static World CreateWorld()
    {
        var size = new Size(100, 72);
        var ground = new TerrainGrid(size);
        ground.Fill(TerrainPixel.Blank);
        var world = new World(size, rules: new MatchRules { TerrainRegrowth = false });
        world.Initialize(ground, [new Position(25, 36), new Position(75, 36)]);
        return world;
    }

    [Fact]
    public void CountSolidCells_CountsRockAndDirt_LeavesOutTheEnds_AndStopsAtTheLimit()
    {
        var ground = new TerrainGrid(new Size(40, 10));
        ground.Fill(TerrainPixel.Blank);
        Position from = new(2, 5), to = new(30, 5);
        Assert.Equal(0, Raycaster.CountSolidCells(ground, from, to, 64));

        for (int x = 10; x < 14; x++)
            ground.SetPixelRaw(new Position(x, 5), TerrainPixel.Rock);
        for (int x = 20; x < 23; x++)
            ground.SetPixelRaw(new Position(x, 5), TerrainPixel.Dirt);
        ground.SetPixelRaw(from, TerrainPixel.Rock);
        ground.SetPixelRaw(to, TerrainPixel.Rock);

        Assert.Equal(7, Raycaster.CountSolidCells(ground, from, to, 64));
        Assert.Equal(7, Raycaster.CountSolidCells(ground, to, from, 64));
        Assert.Equal(5, Raycaster.CountSolidCells(ground, from, to, 5));
    }

    [Fact]
    public void GroundInTheWay_MakesASoundQuieterAndDuller_TheThickerTheMore()
    {
        Heard clear = SoundSpace.Hear(60f, 0f, 0);
        Heard thin = SoundSpace.Hear(60f, 0f, 3);
        Heard thick = SoundSpace.Hear(60f, 0f, 40);

        Assert.Equal(0f, clear.Muffle);
        Assert.InRange(thin.Muffle, 0.05f, 0.4f);
        Assert.True(thick.Muffle > 0.9f);
        Assert.True(clear.Gain > thin.Gain && thin.Gain > thick.Gain && thick.Gain > 0f);
        Assert.Equal(clear.Pan, thick.Pan);

        Assert.Equal(SoundSpace.ClearCutoffHz, SoundSpace.CutoffHz(0f));
        Assert.True(SoundSpace.CutoffHz(thin.Muffle) > SoundSpace.CutoffHz(thick.Muffle));
        Assert.Equal(AudioTweaks.MuffledCutoffHz, SoundSpace.CutoffHz(1f), 1f);
    }

    [Fact]
    public void Pan_FollowsTheDirection_NotTheDistanceAlone()
    {
        // A tank a little way off to the left sounds from the left, not from the middle.
        Assert.Equal(-AudioTweaks.MaxPan, SoundSpace.Hear(-40f, 0f, 0).Pan, 3);
        Assert.Equal(AudioTweaks.MaxPan, SoundSpace.Hear(120f, 0f, 0).Pan, 3);
        // Straight above or below is centred, and so is one right beside the tank, almost.
        Assert.Equal(0f, SoundSpace.Hear(0f, 50f, 0).Pan);
        Assert.InRange(SoundSpace.Hear(3f, 0f, 0).Pan, 0f, AudioTweaks.MaxPan * 0.2f);
        // Nearer is louder; at the edge of hearing it is silent.
        Assert.True(SoundSpace.Hear(20f, 0f, 0).Gain > SoundSpace.Hear(80f, 0f, 0).Gain);
        Assert.False(SoundSpace.Hear(400f, 0f, 0).IsAudible);
    }

    [Fact]
    public void ApproachRate_IsHigherComingCloser_LowerGoingAway_AndBounded()
    {
        Assert.Equal(1f, SoundSpace.ApproachRate(0f));
        Assert.True(SoundSpace.ApproachRate(-1f) > 1f);
        Assert.True(SoundSpace.ApproachRate(1f) < 1f);
        Assert.Equal(1f + AudioTweaks.MaxApproachPitch, SoundSpace.ApproachRate(-100f));
    }

    [Fact]
    public void AnotherTank_BehindRock_IsMuffled_AndHeardFromItsSide()
    {
        var world = CreateWorld();
        var sink = new RecordingSink();
        var director = new SoundDirector(sink);

        director.AfterStep(world, localSlot: 0);
        var open = sink.Loop(1, SoundEffect.EngineLoop);
        Assert.True(open.Gain > 0f);
        // Only the two bases' walls stand between the tanks: a little muffled, no more.
        Assert.InRange(open.Muffle, 0f, 0.3f);
        Assert.True(open.Pan > 0f, "The other tank is to the right.");
        Assert.Equal(0f, sink.Loop(0, SoundEffect.EngineLoop).Muffle);

        for (int y = 20; y < 52; y++)
            for (int x = 45; x < 55; x++)
                world.Terrain.SetPixelRaw(new Position(x, y), TerrainPixel.Rock);
        director.AfterStep(world, localSlot: 0);
        var walled = sink.Loop(1, SoundEffect.EngineLoop);

        Assert.True(walled.Muffle > open.Muffle + 0.4f, $"muffle {open.Muffle} -> {walled.Muffle}");
        Assert.True(walled.Gain < open.Gain);
        Assert.Equal(open.Pan, walled.Pan);
    }

    [Fact]
    public void AnotherTankDrivingCloser_ClattersItsTreads_AtARisingPitch_ButThePlayersOwnDoNot()
    {
        var world = CreateWorld();
        var sink = new RecordingSink();
        var director = new SoundDirector(sink);
        var towardsPlayer = new ControllerOutput { MoveSpeed = new Offset(-1, 0) };

        for (int i = 0; i < 12; i++)
        {
            world.Advance(slot => slot == 1 ? towardsPlayer : default);
            director.AfterStep(world, localSlot: 0);
        }

        var treads = sink.Loop(1, SoundEffect.TreadLoop);
        Assert.True(treads.Gain > 0f, "The moving tank's treads are heard.");
        Assert.True(treads.Rate > 1f - 0.05f, $"rate {treads.Rate}: coming closer raises the pitch");
        Assert.Equal(0f, sink.Loop(0, SoundEffect.TreadLoop).Gain);

        for (int i = 0; i < 6; i++)
        {
            world.Advance(_ => default);
            director.AfterStep(world, localSlot: 0);
        }
        Assert.Equal(0f, sink.Loop(1, SoundEffect.TreadLoop).Gain);
    }
}
