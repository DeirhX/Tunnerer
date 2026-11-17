namespace Tunnerer.Browser;

using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using Tunnerer.Browser.Client;
using Tunnerer.Core.Input;
using Tunnerer.Core.Types;

public static partial class BrowserApi
{
    private static BrowserSession? _session;

    [JSExport]
    public static void Start(int seed) => _session = new BrowserSession(seed);

    [JSExport]
    public static void Step(int x, int y, bool fire, double aimX, double aimY, int build, int upgrade)
    {
        float ax = (float)aimX, ay = (float)aimY;
        float length = MathF.Sqrt(ax * ax + ay * ay);
        var input = new ControllerOutput
        {
            MoveSpeed = new Offset(Math.Clamp(x, -1, 1), Math.Clamp(y, -1, 1)),
            ShootPrimary = fire,
            AimDirection = length > 0 && float.IsFinite(length) ? new DirectionF(ax / length, ay / length) : default,
            BuildPrimary = build == 1,
            BuildSecondary = build == 2,
            BuildTertiary = build == 3,
            Upgrade = Math.Clamp(upgrade, 0, 6),
        };
        Session.Advance(input);
    }

    /// <summary>
    /// Builds a frame and returns it as one buffer (layout: <see cref="BrowserScene"/>). The view is of the runtime's
    /// own memory: the caller copies it out and disposes it before calling in again.
    /// </summary>
    [JSExport]
    [return: JSMarshalAs<JSType.MemoryView>]
    public static ArraySegment<byte> Render(int width, int height, int scale, double alpha, double delta, double time)
    {
        Session.Scene.Render(width, height, scale, (float)alpha, (float)delta, (float)time);
        return Session.Scene.Packet;
    }

    [JSExport]
    public static string GetFrame(bool includeEvents) =>
        JsonSerializer.Serialize(BrowserFrameBuilder.Capture(Session, includeEvents), BrowserJsonContext.Default.BrowserFrame);

    private static BrowserSession Session => _session ?? throw new InvalidOperationException("Start a match first.");
}
