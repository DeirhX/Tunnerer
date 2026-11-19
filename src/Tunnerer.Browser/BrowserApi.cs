namespace Tunnerer.Browser;

using System.Buffers.Binary;
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
    private static BrowserNetMatch? _net;
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
        _net = null;
        _session = new BrowserSession(seed, heatWorkers,
            width > 0 && height > 0 ? new Size(width, height) : null, Math.Max(0, machines));
        _hudStale = true;
        _dialSent = false;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Joins <paramref name="room"/> on the relay, or opens it with this browser's proposal if nobody has
    /// (<see cref="BrowserNetMatch"/>). The page owns the socket: the hello comes back through
    /// <see cref="DeliverNet"/> for it to send, and the world is built when the relay lets this client in.
    /// </summary>
    /// <param name="players">The tanks of the match, if this browser opens the room.</param>
    /// <param name="seed">The seed it proposes if it opens the room.</param>
    /// <param name="width">The map it proposes (<c>?map=WxH</c>); 0 for the desktop's map for the player count.</param>
    [JSExport]
    public static Task Join(string room, string name, string secret, int players, int seed, int width, int height,
        int heatWorkers)
    {
        _session = null;
        _net = new BrowserNetMatch(room, name, secret,
            BrowserNetMatch.Propose(seed, players, width > 0 && height > 0 ? new Size(width, height) : null),
            Math.Max(1, heatWorkers));
        _hudStale = true;
        _dialSent = false;
        DeliverOutgoing(_net);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Takes in what the page's socket received since the last call and why it closed, if it did. Each message is
    /// laid out as its arrival time on the page's clock in milliseconds (a little-endian double), its length (a
    /// little-endian int32) and its bytes.
    /// </summary>
    /// <returns>
    /// Null once this player's world is built, when the page asks for frames; until then why the match cannot be
    /// joined, or an empty string while the relay has not answered.
    /// </returns>
    [JSExport]
    public static Task<string?> Receive(byte[] received, string? lost)
    {
        ArgumentNullException.ThrowIfNull(received);
        BrowserNetMatch net = _net ?? throw new InvalidOperationException("Join a room first.");
        var span = received.AsSpan();
        while (span.Length >= 12)
        {
            double arrived = BinaryPrimitives.ReadDoubleLittleEndian(span);
            int length = BinaryPrimitives.ReadInt32LittleEndian(span[8..]);
            if (length < 0 || length > span.Length - 12)
                throw new InvalidDataException("A received message runs past the end of the batch.");
            net.Receive(span.Slice(12, length), TimeSpan.FromMilliseconds(arrived));
            span = span[(12 + length)..];
        }
        if (lost != null)
            net.Lose(lost);
        DeliverOutgoing(net);
        return Task.FromResult(net.Session != null ? null : net.Failure ?? string.Empty);
    }

    /// <summary>A request of the player to the room: <c>start</c> (the host starts the match), <c>pause</c> (for everyone, or resume), or <c>close</c> / <c>open</c> (the host shuts the running match to newcomers, or reopens it).</summary>
    [JSExport]
    public static Task Command(string command)
    {
        BrowserNetMatch net = _net ?? throw new InvalidOperationException("Join a room first.");
        if (command == "start")
            net.RequestStart();
        else if (command == "pause")
            net.TogglePause();
        else if (command == "close" || command == "open")
            net.SetClosedToNewcomers(command == "close");
        DeliverOutgoing(net);
        return Task.CompletedTask;
    }

    /// <summary>Hands what <paramref name="net"/> has to send to the page, each message as its length (int32) and bytes.</summary>
    private static void DeliverOutgoing(BrowserNetMatch net)
    {
        var outgoing = net.TakeOutgoing();
        if (outgoing.Count == 0)
            return;
        byte[] packed = new byte[outgoing.Sum(m => 4 + m.Length)];
        int at = 0;
        foreach (byte[] message in outgoing)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(at), message.Length);
            message.CopyTo(packed, at + 4);
            at += 4 + message.Length;
        }
        DeliverNet(packed);
    }

    /// <summary>Hands messages for the relay to the page's socket (frames.js), which copies them before it returns.</summary>
    [JSImport("deliverNet", "frames.js")]
    private static partial void DeliverNet([JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> packed);

    /// <summary>
    /// One display request: steps the simulation, builds a frame and hands it to <see cref="Deliver"/> before the
    /// promise resolves. A local match steps once if <paramref name="step"/>, with the input given. A network match
    /// takes the input whenever it is given, and steps as the relay's ticks have arrived (<paramref name="due"/>:
    /// the page's clock says a step is due). <paramref name="thermalView"/> is the player's thermal view (H,
    /// <see cref="BrowserSession.ThermalView"/>), which only changes what is drawn. <paramref name="now"/> is the
    /// page's clock in milliseconds, which <see cref="Receive"/> times arrivals by.
    /// </summary>
    [JSExport]
    public static Task Frame(bool step, int x, int y, bool fire, double aimX, double aimY, int build,
        int upgrade, int width, int height, int scale, double alpha, double delta, double time, bool thermalView,
        bool due, double now)
    {
        BrowserSession session = _net?.Session ?? _session ?? throw new InvalidOperationException("Start a match first.");
        session.ThermalView = thermalView;
        long began = Stopwatch.GetTimestamp();
        var input = step ? Input(x, y, fire, aimX, aimY, build, upgrade) : default;
        int steps = 0;
        if (_net is { } net)
        {
            steps = net.Advance(input, due ? 1 : 0, TimeSpan.FromMilliseconds(now));
            // A step just taken is drawn from where it began: the ticks arrive by the relay's clock, not the page's.
            if (steps > 0)
                alpha = 0;
            _hudStale |= steps > 0 || net.Changed;
            DeliverOutgoing(net);
        }
        else if (step)
        {
            session.Advance(input);
            steps = 1;
            _hudStale = true;
        }

        long stepped = Stopwatch.GetTimestamp();
        session.Scene.Render(width, height, scale, (float)alpha, (float)delta, (float)time);
        string? hud = null;
        if (_hudStale)
        {
            hud = JsonSerializer.Serialize(BrowserFrameBuilder.Capture(session, includeEvents: steps > 0,
                includeDial: !_dialSent, _net), BrowserJsonContext.Default.BrowserFrame);
            _dialSent = true;
            _hudStale = false;
        }

        Deliver(session.Scene.Packet, hud, steps > 0 ? Stopwatch.GetElapsedTime(began, stepped).TotalMilliseconds : -1,
            Stopwatch.GetElapsedTime(stepped).TotalMilliseconds, steps);
        return Task.CompletedTask;
    }

    private static ControllerOutput Input(int x, int y, bool fire, double aimX, double aimY, int build, int upgrade)
    {
        float ax = (float)aimX, ay = (float)aimY;
        float length = MathF.Sqrt(ax * ax + ay * ay);
        return new ControllerOutput
        {
            MoveSpeed = new Offset(Math.Clamp(x, -1, 1), Math.Clamp(y, -1, 1)),
            ShootPrimary = fire,
            AimDirection = length > 0 && float.IsFinite(length) ? new DirectionF(ax / length, ay / length) : default,
            BuildPrimary = build == 1,
            BuildSecondary = build == 2,
            BuildTertiary = build == 3,
            Upgrade = Math.Clamp(upgrade, 0, 6),
        };
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
    /// <param name="steps">How many simulation steps the request took.</param>
    [JSImport("deliver", "frames.js")]
    private static partial void Deliver([JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> packet, string? hud,
        double stepMs, double frameMs, int steps);

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
