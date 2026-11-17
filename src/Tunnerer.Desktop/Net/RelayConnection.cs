namespace Tunnerer.Desktop.Net;

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Threading.Channels;
using Tunnerer.Protocol;

/// <summary>
/// This client's WebSocket connection to the relay. Two background tasks read and write the socket; the game
/// thread only ever touches queues, through <see cref="Pump"/> and the send methods, so nothing here blocks a frame
/// and no game state is touched off the game thread.
/// </summary>
public sealed class RelayConnection : ILockstepLink, IDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly CancellationTokenSource _closing = new();
    private readonly ConcurrentQueue<(Message Message, TimeSpan Arrived)> _inbox = new();
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Queue<TickBundle> _bundles = new();
    private volatile string? _lost;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private TimeSpan _pingSentAt;
    private uint _pingId;
    private bool _pingPending;

    /// <summary>
    /// How the client notices a relay that has gone silent without closing the connection: after
    /// <see cref="Interval"/> of silence its socket pings the relay, and if nothing at all comes back within
    /// <see cref="Timeout"/> the connection is ended and <see cref="Failure"/> says so. The socket does this itself,
    /// on its own background task, so a game loop that is busy for a long time (building the world on a slow
    /// machine) neither sends the pings nor delays the answers to the relay's. A lobby or a paused match, where
    /// no ticks flow, passes the same way: the relay answers every ping.
    /// </summary>
    public sealed record KeepAlive(TimeSpan Interval, TimeSpan Timeout)
    {
        public static KeepAlive Default { get; } = new(TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(10));
    }

    private RelayConnection(ClientWebSocket socket, Welcome welcome, string room)
    {
        _socket = socket;
        Room = room;
        Slot = welcome.Slot;
        Config = welcome.Config;
        Roster = [];
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(WriteLoopAsync);
    }

    /// <summary>The room this client is in.</summary>
    public string Room { get; }

    /// <summary>The slot the relay gave this client: which tank it drives.</summary>
    public int Slot { get; }

    /// <summary>The match every client in the room builds.</summary>
    public MatchConfig Config { get; }

    /// <summary>The players' names by slot; an empty name is an empty slot.</summary>
    public ImmutableArray<string> Roster { get; private set; }

    /// <summary>True once the relay has started the match: ticks are coming.</summary>
    public bool Started { get; private set; }

    /// <summary>Once started: bit n is set when a player drives slot n; the other slots are for bots.</summary>
    public int HumanMask { get; private set; }

    /// <summary>The slot of the host, who may start the match before the room is full: the lowest slot taken.</summary>
    public int HostSlot => RoomRules.HostSlot(Roster.Length, slot => Roster[slot].Length > 0);

    public bool IsHost => HostSlot == Slot;

    /// <summary>True when this client came back to a match that was already running, and is replaying it.</summary>
    public bool Rejoined { get; private set; }

    /// <summary>True while the match is held for everyone; no ticks come until someone resumes it.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>The slot of whoever paused or resumed last.</summary>
    public int PausedBySlot { get; private set; }

    /// <summary>The last measured round trip to the relay and back, in milliseconds; null before the first.</summary>
    public int? LatencyMs { get; private set; }

    /// <summary>The tick at which the relay found two clients' worlds different, if it has.</summary>
    public int? DesyncTick { get; private set; }

    /// <summary>Why the connection is over (refused, or lost), or null while it is alive.</summary>
    public string? Failure { get; private set; }

    /// <summary>
    /// Connects, says hello and waits for the relay's answer. Blocks, so call it before the game loop starts.
    /// </summary>
    /// <exception cref="RelayException">The relay could not be reached, or it refused the request.</exception>
    public static RelayConnection Connect(Uri relay, Hello hello, TimeSpan timeout, KeepAlive? keepAlive = null)
    {
        keepAlive ??= KeepAlive.Default;
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = keepAlive.Interval;
        socket.Options.KeepAliveTimeout = keepAlive.Timeout;
        try
        {
            using var limit = new CancellationTokenSource(timeout);
            socket.ConnectAsync(relay, limit.Token).GetAwaiter().GetResult();
            socket.SendAsync(MessageCodec.Encode(hello), WebSocketMessageType.Binary, true, limit.Token)
                .GetAwaiter().GetResult();
            Message? answer = ReadAsync(socket, new byte[MessageCodec.MaxMessageBytes], limit.Token).GetAwaiter().GetResult();
            return answer switch
            {
                Welcome welcome => new RelayConnection(socket, welcome, hello.Room),
                Refusal refusal => throw new RelayException(refusal.Reason),
                _ => throw new RelayException("The relay closed the connection without an answer."),
            };
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or InvalidDataException)
        {
            socket.Dispose();
            throw new RelayException($"Could not reach the relay at {relay}: {e.Message}", e);
        }
        catch (RelayException)
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Takes in everything the relay has sent since the last call. Call once per frame, on the game thread.</summary>
    public void Pump()
    {
        while (_inbox.TryDequeue(out var received))
        {
            switch (received.Message)
            {
                case Tick tick: _bundles.Enqueue(tick.Bundle); break;
                case Roster roster: Roster = roster.Names; break;
                case Start start:
                    Started = true;
                    HumanMask = start.HumanMask;
                    break;
                case Rejoined rejoined:
                    Started = true;
                    Rejoined = true;
                    HumanMask = rejoined.HumanMask;
                    break;
                case Log log:
                    foreach (var bundle in log.Bundles())
                        _bundles.Enqueue(bundle);
                    break;
                case PauseState pause:
                    IsPaused = pause.Paused;
                    PausedBySlot = pause.BySlot;
                    break;
                case Pong pong when _pingPending && pong.Id == _pingId:
                    _pingPending = false;
                    // Timed to when the answer arrived, not to when the game loop got round to reading it.
                    LatencyMs = (int)Math.Round((received.Arrived - _pingSentAt).TotalMilliseconds);
                    break;
                case Desync desync: DesyncTick ??= desync.AtTick; break;
                case Refusal refusal: Failure ??= refusal.Reason; break;
            }
        }
        if (_inbox.IsEmpty && _lost is { } lost)
            Failure ??= lost;

        // One ping a second; a lost one is given up on after five.
        TimeSpan sincePing = _clock.Elapsed - _pingSentAt;
        if (Failure == null && (_pingPending ? sincePing > PingTimeout : sincePing > PingInterval || _pingId == 0))
        {
            _pingSentAt = _clock.Elapsed;
            _pingPending = true;
            Send(new Ping(++_pingId));
        }
    }

    /// <summary>
    /// What to tell the player when the match is not simply running: waiting for players, out of sync, or over.
    /// Null while it plays.
    /// </summary>
    public string? Status
    {
        get
        {
            if (Failure is { } failure)
                return Started && DesyncTick == null
                    ? $"Disconnected: {failure} Press Enter to reconnect and rejoin the match."
                    : $"Disconnected: {failure}";
            if (DesyncTick is int tick)
                return $"The players' games went out of sync at tick {tick}. The match cannot continue.";
            if (!Started)
                return $"Waiting for players: {PlayersHere} of {Config.Slots} here.";
            if (IsPaused)
            {
                string by = PausedBySlot < Roster.Length && Roster[PausedBySlot].Length > 0 ? Roster[PausedBySlot] : "a player";
                return $"Paused by {by}. P resumes the match for everyone.";
            }
            return null;
        }
    }

    /// <summary>How many players are in the room now.</summary>
    public int PlayersHere => Roster.Count(name => name.Length > 0);

    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(5);

    public void SendReady(ulong worldHash) => Send(new Ready(worldHash));

    /// <summary>Asks the relay to hold the match for everyone, or to let it run again.</summary>
    public void SetPaused(bool paused) => Send(new SetPaused(paused));

    /// <summary>Asks the relay to start with the players who are here. Only the host's request counts.</summary>
    public void RequestStart() => Send(new StartRequest());

    public void SendInput(uint input) => Send(new Input(input));

    public void SendHash(int tick, ulong hash) => Send(new Hash(tick, hash));

    public void SendMatchOver(int tick) => Send(new MatchOver(tick));

    public bool TryReceive([NotNullWhen(true)] out TickBundle? bundle) => _bundles.TryDequeue(out bundle);

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _closing.Cancel();
        _outbox.Writer.TryComplete();
        _socket.Abort();
        _socket.Dispose();
        _closing.Dispose();
    }

    private void Send(Message message) => _outbox.Writer.TryWrite(MessageCodec.Encode(message));

    private async Task ReadLoopAsync()
    {
        try
        {
            byte[] buffer = new byte[MessageCodec.MaxMessageBytes];
            while (await ReadAsync(_socket, buffer, _closing.Token).ConfigureAwait(false) is { } message)
                _inbox.Enqueue((message, _clock.Elapsed));
            _lost = "the relay closed the connection.";
        }
        catch (InvalidDataException e)
        {
            _lost = $"the relay sent something this game does not understand ({e.Message}).";
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Also what a relay that went silent ends in: the socket gave up waiting for its answer to a ping.
            _lost = "lost the connection to the relay.";
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (byte[] bytes in _outbox.Reader.ReadAllAsync(_closing.Token).ConfigureAwait(false))
                await _socket.SendAsync(bytes, WebSocketMessageType.Binary, true, _closing.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The read loop reports the loss.
        }
    }

    private static async Task<Message?> ReadAsync(ClientWebSocket socket, byte[] buffer, CancellationToken cancel) =>
        await MessageFraming.ReadFrameAsync(socket, buffer, cancel).ConfigureAwait(false) is { } frame
            ? MessageCodec.Decode(frame)
            : null;
}

/// <summary>The relay could not be reached or refused this client; the message is for the player.</summary>
public sealed class RelayException : Exception
{
    public RelayException(string message) : base(message)
    {
    }

    public RelayException(string message, Exception inner) : base(message, inner)
    {
    }

    public RelayException()
    {
    }
}
