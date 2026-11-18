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
    private readonly RoomClient _room;
    private volatile string? _lost;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

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

    private RelayConnection(ClientWebSocket socket, Welcome welcome, Hello hello, Uri address)
    {
        _socket = socket;
        Address = address;
        _room = new RoomClient(hello, welcome, Send);
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(WriteLoopAsync);
    }

    /// <summary>The relay's address.</summary>
    public Uri Address { get; }

    /// <summary>What the relay has said to this client: <see cref="RoomClient"/>.</summary>
    public string Room => _room.Room;

    /// <inheritdoc cref="RoomClient.RequestedName"/>
    public string RequestedName => _room.RequestedName;

    /// <inheritdoc cref="RoomClient.AssignedName"/>
    public string? AssignedName => _room.AssignedName;

    /// <inheritdoc cref="RoomClient.Slot"/>
    public int Slot => _room.Slot;

    /// <inheritdoc cref="RoomClient.Config"/>
    public MatchConfig Config => _room.Config;

    /// <inheritdoc cref="RoomClient.Roster"/>
    public ImmutableArray<string> Roster => _room.Roster;

    /// <inheritdoc cref="RoomClient.Started"/>
    public bool Started => _room.Started;

    /// <inheritdoc cref="RoomClient.HumanMask"/>
    public int HumanMask => _room.HumanMask;

    /// <inheritdoc cref="RoomClient.HostSlot"/>
    public int HostSlot => _room.HostSlot;

    public bool IsHost => _room.IsHost;

    /// <inheritdoc cref="RoomClient.Rejoined"/>
    public bool Rejoined => _room.Rejoined;

    /// <inheritdoc cref="RoomClient.IsPaused"/>
    public bool IsPaused => _room.IsPaused;
    public bool IsHeld => IsPaused || _room.JoiningName.Length > 0;
    public string DriverName(int slot, int humanMask) => _room.DriverName(slot, humanMask);

    /// <inheritdoc cref="RoomClient.PausedBySlot"/>
    public int PausedBySlot => _room.PausedBySlot;

    /// <inheritdoc cref="RoomClient.LatencyMs"/>
    public int? LatencyMs => _room.LatencyMs;

    /// <inheritdoc cref="RoomClient.DesyncTick"/>
    public int? DesyncTick => _room.DesyncTick;

    /// <inheritdoc cref="RoomClient.Failure"/>
    public string? Failure => _room.Failure;

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
                Welcome { Slot: -1 } when !hello.Snapshots.HasFlag(SnapshotCapabilities.Join) =>
                    throw new RelayException("The relay assigned a watcher to a client that requested a player seat."),
                Welcome welcome => new RelayConnection(socket, welcome, hello, relay),
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
            _room.Accept(received.Message, received.Arrived);
        if (_inbox.IsEmpty && _lost is { } lost)
            _room.Lose(lost);
        _room.KeepTime(_clock.Elapsed);
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
            if (_room.JoiningName.Length > 0)
                return $"Copying the match to {_room.JoiningName}.";
            if (IsPaused)
                return $"Paused by {_room.PausedByName}. P resumes the match for everyone.";
            return null;
        }
    }

    /// <inheritdoc cref="RoomClient.PlayersHere"/>
    public int PlayersHere => _room.PlayersHere;

    public void SendReady(ulong worldHash) => _room.SendReady(worldHash);

    /// <inheritdoc cref="RoomClient.SetPaused"/>
    public void SetPaused(bool paused) => _room.SetPaused(paused);

    /// <inheritdoc cref="RoomClient.RequestStart"/>
    public void RequestStart() => _room.RequestStart();

    public void SendInput(uint input) => _room.SendInput(input);

    public void SendHash(int tick, ulong hash) => _room.SendHash(tick, hash);

    public void SendMatchOver(int tick) => _room.SendMatchOver(tick);

    public bool TryReceive([NotNullWhen(true)] out TickBundle? bundle) => _room.TryReceive(out bundle);

    /// <inheritdoc cref="RoomClient.SendJoin"/>
    public void SendJoin(Message message) => _room.SendJoin(message);

    public bool TryReceiveJoin([NotNullWhen(true)] out Message? message) => _room.TryReceiveJoin(out message);

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
            byte[] buffer = new byte[MessageCodec.MaxFrameBytes];
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
