namespace Tunnerer.Browser;

using System.Diagnostics;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using Tunnerer.Browser.Client;
using Tunnerer.Core.Input;
using Tunnerer.Core.Types;

/// <summary>
/// What JavaScript calls (frames.js). Every export answers with a promise: the threaded runtime is called from the
/// page's own thread, where .NET takes only calls that answer later, and the single-thread runtime in its worker is
/// called the same way so that there is one way in. A promise carries no buffer, so a frame is handed over by
/// calling JavaScript back (<see cref="Deliver"/>).
/// </summary>
public static partial class BrowserApi
{
    private static BrowserSession? _session;
    private static bool _hudStale;
    private static bool _dialSent;

    /// <param name="seed">The world's seed.</param>
    /// <param name="heatWorkers">Threads for the heat solver; 1 on the single-thread runtime.</param>
    /// <param name="width">The map's width in cells (<c>?map=WxH</c>); 0 for the normal map.</param>
    /// <param name="height">The map's height in cells; 0 for the normal map.</param>
    /// <param name="machines">For measuring (<c>?machines=N</c>): machines planted before play; 0 in a normal match.</param>
    [JSExport]
    public static Task Start(int seed, int heatWorkers, int width, int height, int machines)
    {
        _session = new BrowserSession(seed, heatWorkers,
            width > 0 && height > 0 ? new Size(width, height) : null, Math.Max(0, machines));
        _hudStale = true;
        _dialSent = false;
        return Task.CompletedTask;
    }

    /// <summary>
    /// One display request: steps the simulation once if <paramref name="step"/>, builds a frame and hands it to
    /// <see cref="Deliver"/> before the promise resolves. <paramref name="thermalView"/> is the player's thermal
    /// view (H, <see cref="BrowserSession.ThermalView"/>), which only changes what is drawn.
    /// </summary>
    [JSExport]
    public static Task Frame(bool step, int x, int y, bool fire, double aimX, double aimY, int build,
        int upgrade, int width, int height, int scale, double alpha, double delta, double time, bool thermalView)
    {
        BrowserSession session = _session ?? throw new InvalidOperationException("Start a match first.");
        session.ThermalView = thermalView;
        long began = Stopwatch.GetTimestamp();
        if (step)
        {
            float ax = (float)aimX, ay = (float)aimY;
            float length = MathF.Sqrt(ax * ax + ay * ay);
            session.Advance(new ControllerOutput
            {
                MoveSpeed = new Offset(Math.Clamp(x, -1, 1), Math.Clamp(y, -1, 1)),
                ShootPrimary = fire,
                AimDirection = length > 0 && float.IsFinite(length) ? new DirectionF(ax / length, ay / length) : default,
                BuildPrimary = build == 1,
                BuildSecondary = build == 2,
                BuildTertiary = build == 3,
                Upgrade = Math.Clamp(upgrade, 0, 6),
            });
            _hudStale = true;
        }

        long stepped = Stopwatch.GetTimestamp();
        session.Scene.Render(width, height, scale, (float)alpha, (float)delta, (float)time);
        string? hud = null;
        if (_hudStale)
        {
            hud = JsonSerializer.Serialize(BrowserFrameBuilder.Capture(session, includeEvents: step, includeDial: !_dialSent),
                BrowserJsonContext.Default.BrowserFrame);
            _dialSent = true;
            _hudStale = false;
        }

        Deliver(session.Scene.Packet, hud, step ? Stopwatch.GetElapsedTime(began, stepped).TotalMilliseconds : -1,
            Stopwatch.GetElapsedTime(stepped).TotalMilliseconds);
        return Task.CompletedTask;
    }

    /// <summary>Hands a frame to JavaScript (frames.js), which copies the packet before it returns.</summary>
    /// <param name="packet">
    /// The frame's packet (layout: <see cref="BrowserScene"/>): a view of the runtime's own memory, good for the
    /// length of the call.
    /// </param>
    /// <param name="hud">
    /// The HUD's state as JSON, or null when it has not changed since the last frame; it changes only with a step.
    /// </param>
    /// <param name="stepMs">The milliseconds the simulation step took; negative when there was none.</param>
    /// <param name="frameMs">The milliseconds building the frame took.</param>
    [JSImport("deliver", "frames.js")]
    private static partial void Deliver([JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> packet, string? hud,
        double stepMs, double frameMs);

    /// <summary>
    /// Synthesises the desktop's sound bank and hands it to <see cref="DeliverSounds"/> before the promise resolves.
    /// The page asks for it once, when the player first turns sound on.
    /// </summary>
    [JSExport]
    public static Task SoundBank()
    {
        var bank = new BrowserSoundBank();
        DeliverSounds(bank.ToBytes(), JsonSerializer.Serialize(bank.Layout, BrowserJsonContext.Default.SoundBankLayout));
        return Task.CompletedTask;
    }

    /// <summary>Hands the sound bank to JavaScript (frames.js), which copies the samples before it returns.</summary>
    /// <param name="samples">Every effect's samples (32-bit floats) one after another; a view good for the call.</param>
    /// <param name="layout">Where each effect lies (<see cref="SoundBankLayout"/>), as JSON.</param>
    [JSImport("deliverSounds", "frames.js")]
    private static partial void DeliverSounds([JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> samples, string layout);

    /// <summary>
    /// Plays <paramref name="ticks"/> steps of a match nobody watches (the player idle, the bot playing) and returns
    /// its <see cref="BrowserSession.Fingerprint"/> in hexadecimal: how a browser, a runtime or a number of heat
    /// workers is shown to compute the same world as another. The match being played is left alone.
    /// </summary>
    [JSExport]
    public static Task<string> Fingerprint(int seed, int heatWorkers, int ticks)
    {
        var session = new BrowserSession(seed, heatWorkers);
        for (int i = 0; i < ticks; i++)
            session.Advance(default);
        return Task.FromResult(session.Fingerprint().ToString("X16", System.Globalization.CultureInfo.InvariantCulture));
    }
}
