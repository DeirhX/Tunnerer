using Tunnerer.Core.Types;
using Tunnerer.Desktop.Rendering.Entities;

namespace Tunnerer.Tests;

public class BeamFaderTests
{
    private static readonly BeamKey Key = new(EntityShape.KindLink, new Position(1, 2), new Position(9, 2));

    private static float Run(BeamFader beams, ref float seconds, float duration, bool present)
    {
        for (float end = seconds + duration; seconds < end - 1e-4f;)
        {
            seconds += 1f / 60f;
            beams.Begin(seconds);
            if (present)
                beams.Submit(Key, 1, 2, 9, 2, 0f, 1.2f);
        }
        return beams.TransitionOf(Key) ?? 0f;
    }

    [Fact]
    public void NewBeam_GrowsToWholeOverConnectSeconds()
    {
        var beams = new BeamFader();
        float t = 0f;
        float halfway = Run(beams, ref t, BeamFader.ConnectSeconds * 0.5f, present: true);
        Assert.InRange(halfway, 0.3f, 0.7f);
        Assert.Equal(1f, Run(beams, ref t, BeamFader.ConnectSeconds, present: true));
    }

    [Fact]
    public void BrokenBeam_DissolvesThenIsForgotten()
    {
        var beams = new BeamFader();
        float t = 0f;
        Run(beams, ref t, BeamFader.ConnectSeconds + 0.1f, present: true);

        // A blip shorter than the grace does not break it.
        Assert.Equal(1f, Run(beams, ref t, BeamFader.GraceSeconds * 0.5f, present: false));
        Assert.Equal(1f, Run(beams, ref t, 0.05f, present: true));

        float dissolving = Run(beams, ref t, BeamFader.GraceSeconds + BeamFader.DissolveSeconds * 0.5f, present: false);
        Assert.InRange(dissolving, -0.7f, -0.3f);
        Run(beams, ref t, BeamFader.DissolveSeconds, present: false);
        Assert.Null(beams.TransitionOf(Key));
        Assert.Equal(0, beams.Count);
    }

    [Fact]
    public void ReconnectingAfterABreak_GrowsAgainFromTheSource()
    {
        var beams = new BeamFader();
        float t = 0f;
        Run(beams, ref t, BeamFader.ConnectSeconds + 0.1f, present: true);
        Run(beams, ref t, BeamFader.GraceSeconds + 0.2f, present: false);
        Assert.InRange(Run(beams, ref t, 1f / 60f, present: true), 0f, 0.1f);
    }
}
