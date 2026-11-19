namespace Emberward.Relay;

using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading.Channels;
using Emberward.Protocol;

/// <summary>
/// The rooms of this relay and the WebSocket side of each client: reads its messages into its room, writes the
/// room's messages back, and runs each running room's tick clock.
/// </summary>
public sealed class RelayHub(ILogger<RelayHub> logger, IConfiguration configuration)
{
    /// <summary>
    /// The configuration key (and so the environment variable) holding the shared secret. When it is set, a client
    /// must send the same secret to get in; when it is not, the relay is open. Spaces and line ends around either
    /// are not part of it: they come with a pasted secret, or with a line of an environment file.
    /// </summary>
    public const string SecretKey = "EMBERWARD_RELAY_SECRET";

    private readonly byte[] _secret = System.Text.Encoding.UTF8.GetBytes(CheckedSecret(configuration));

    public const int MaxRooms = 64;
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Most messages one connection may send in a second, before the relay ends it. An honest client sends one input
    /// per step (at most a few steps per frame while it catches up, about 240 a second at 60 frames) and a ping and
    /// a hash a second; this is several times that.
    /// </summary>
    public const int MaxMessagesPerSecond = 2000;

    /// <summary>
    /// How long a started match whose players have all left is kept for them to come back to. Its clock waits
    /// meanwhile. A room still in the lobby is forgotten as soon as it is empty.
    /// </summary>
    public TimeSpan EmptyRoomGrace { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The shared secret from the configuration (empty when there is none).</summary>
    /// <exception cref="InvalidOperationException">The secret is longer than a client can send.</exception>
    public static string CheckedSecret(IConfiguration configuration)
    {
        string secret = (configuration[SecretKey] ?? string.Empty).Trim();
        if (secret.Length > MessageCodec.MaxSecretLength)
            throw new InvalidOperationException(
                $"{SecretKey} is {secret.Length} characters long; the protocol carries at most {MessageCodec.MaxSecretLength}. " +
                "Use a shorter secret.");
        return secret;
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<string, MatchRoom> _rooms = new(StringComparer.OrdinalIgnoreCase);

    public int RoomCount
    {
        get
        {
            lock (_lock)
                return _rooms.Count;
        }
    }

    /// <summary>Serves one client from its first message until its connection ends.</summary>
    public async Task ServeAsync(WebSocket socket, CancellationToken stopping)
    {
        using var peer = new SocketPeer(socket);
        using var served = CancellationTokenSource.CreateLinkedTokenSource(stopping, peer.Closed);
        Task writing = peer.WriteAsync(stopping);
        MatchRoom? room = null;
        int slot = -1;
        // One buffer for the whole connection: each message is decoded before the next is read into it.
        byte[] buffer = new byte[MessageCodec.MaxMessageBytes];
        try
        {
            using var helloLimit = CancellationTokenSource.CreateLinkedTokenSource(served.Token);
            helloLimit.CancelAfter(HelloTimeout);
            if (await MessageFraming.ReadFrameAsync(socket, buffer, helloLimit.Token) is not { } first)
                return;
            // The protocol number is read before the rest: a client of another protocol sends a hello this relay
            // may not be able to decode, and should still be told to update.
            if (MessageCodec.TryReadHelloProtocol(first, out int protocol) && protocol != MessageCodec.ProtocolVersion)
            {
                peer.Send(new Refusal(
                    $"The relay speaks protocol {MessageCodec.ProtocolVersion}; you speak {protocol}. Update the game."));
                return;
            }
            if (MessageCodec.Decode(first) is not Hello hello)
                return;

            (room, slot) = Enter(peer, hello);
            if (room is null)
                return;
            RelayLog.Joined(logger, hello.Name, room.Code, slot);
            if (room.IsRunning)
                StartClock(room, stopping);

            var rate = new MessageRate(MaxMessagesPerSecond);
            while (await MessageFraming.ReadFrameAsync(socket, buffer, served.Token) is { } frame)
            {
                if (rate.Exceeded())
                    throw new InvalidDataException($"The client sends more than {MaxMessagesPerSecond} messages a second.");
                Message message = MessageCodec.Decode(frame);
                room.Handle(slot, message);
                if (message is not (Input or Hash) && room.IsRunning)
                    StartClock(room, stopping);
            }
        }
        catch (Exception e) when (e is InvalidDataException or WebSocketException or OperationCanceledException)
        {
            RelayLog.ConnectionEnded(logger, e.Message);
        }
        finally
        {
            if (room is not null && slot >= 0)
            {
                room.Leave(slot);
                // The last player who was not ready leaving can be what starts the match.
                if (room.IsRunning && !room.IsEmpty)
                    StartClock(room, stopping);
                Forget(room);
            }
            peer.Complete();
            await writing;
        }
    }

    private (MatchRoom? Room, int Slot) Enter(SocketPeer peer, Hello hello)
    {
        string sent = hello.Secret.Trim();
        if (_secret.Length > 0 && !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                _secret, System.Text.Encoding.UTF8.GetBytes(sent)))
        {
            // Both say "needs a secret", which is how the clients know to ask for one.
            RelayLog.Refused(logger, hello.Name, hello.Room,
                sent.Length == 0 ? "no secret" : $"a wrong secret of {sent.Length} characters");
            peer.Send(new Refusal(sent.Length == 0
                ? "This relay needs a secret. Enter it to join."
                : "This relay needs a secret, and the one you sent is not it."));
            return (null, -1);
        }
        if (string.IsNullOrWhiteSpace(hello.Room))
        {
            peer.Send(new Refusal("A room name is needed."));
            return (null, -1);
        }

        // Joining happens under the hub's lock, so a room cannot be forgotten between being found and being joined.
        lock (_lock)
        {
            // A finished match gives its name up: whoever asks for it next opens a new room. Players still looking
            // at the old result stay connected to the old room, which gets no more ticks.
            if (_rooms.TryGetValue(hello.Room, out MatchRoom? finished) && finished.IsOver)
            {
                _rooms.Remove(hello.Room);
                RelayLog.RoomClosed(logger, finished.Code, finished.TicksClosed);
            }
            if (!_rooms.TryGetValue(hello.Room, out MatchRoom? room))
            {
                if (_rooms.Count >= MaxRooms)
                {
                    peer.Send(new Refusal("The relay has no room left."));
                    return (null, -1);
                }
                room = new MatchRoom(hello.Room, hello.BuildId, hello.Config);
                _rooms.Add(hello.Room, room);
                RelayLog.RoomOpened(logger, room.Code, room.Config.Slots);
            }

            int slot = room.Join(peer, hello.Name, hello.BuildId, out string refusal);
            if (slot >= 0)
                return (room, slot);
            peer.Send(new Refusal(refusal));
            return (null, -1);
        }
    }

    /// <summary>
    /// Deletes the room if it is empty: at once while it is a lobby or its match is over, and once it has been
    /// empty for <see cref="EmptyRoomGrace"/> when its match is still on. Returns whether the room is gone.
    /// </summary>
    private bool Forget(MatchRoom room)
    {
        lock (_lock)
        {
            if (!_rooms.TryGetValue(room.Code, out MatchRoom? known) || known != room)
                return true;
            if (!room.IsEmpty || (room.IsRunning && !room.IsOver && room.EmptyFor < EmptyRoomGrace))
                return false;
            _rooms.Remove(room.Code);
            RelayLog.RoomClosed(logger, room.Code, room.TicksClosed);
            return true;
        }
    }

    private readonly HashSet<MatchRoom> _clocked = [];

    /// <summary>
    /// Runs the room's clock, one per room, from the start of its match until the room is deleted. It only measures
    /// wall time and hands it to the room, which decides what counts (<see cref="MatchRoom.AdvanceClock"/>): the
    /// clock keeps running while the room is empty, so a player who comes back finds it going.
    /// </summary>
    private void StartClock(MatchRoom room, CancellationToken stopping)
    {
        lock (_lock)
        {
            // A room that has given its name up (its match is over and a new room has it) gets no clock.
            if (!_rooms.TryGetValue(room.Code, out MatchRoom? known) || known != room || !_clocked.Add(room))
                return;
        }
        _ = Task.Run(async () =>
        {
            long last = Stopwatch.GetTimestamp();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / room.Config.TicksPerSecond));
            try
            {
                while (await timer.WaitForNextTickAsync(stopping))
                {
                    long now = Stopwatch.GetTimestamp();
                    room.AdvanceClock(Stopwatch.GetElapsedTime(last, now));
                    last = now;
                    if (Forget(room))
                        break;
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lock (_lock)
                    _clocked.Remove(room);
            }
        }, stopping);
    }

    /// <summary>One client's socket as a room sees it: sends are queued and written by <see cref="WriteAsync"/>.</summary>
    private sealed class SocketPeer(WebSocket socket) : IPeer, IDisposable
    {
        // Room for the whole log of a long match sent to a returning client. A client that falls this far behind
        // on reading is not coming back; the full queue ends its connection.
        private readonly Channel<byte[]> _outbox = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(16384) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly CancellationTokenSource _overflow = new();
        private readonly CancellationTokenSource _closed = new();
        private volatile bool _closing;

        /// <summary>Cancelled once the room has closed this connection and what it sent has gone out.</summary>
        public CancellationToken Closed => _closed.Token;

        public void Send(Message message)
        {
            if (_closing)
                return;
            if (!_outbox.Writer.TryWrite(MessageCodec.Encode(message)) && !_outbox.Reader.Completion.IsCompleted)
                _overflow.Cancel();
        }

        public void Close()
        {
            _closing = true;
            _outbox.Writer.TryComplete();
        }

        public void Complete() => _outbox.Writer.TryComplete();

        public async Task WriteAsync(CancellationToken stopping)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, _overflow.Token);
            try
            {
                await foreach (byte[] bytes in _outbox.Reader.ReadAllAsync(linked.Token))
                    await socket.SendAsync(bytes, WebSocketMessageType.Binary, endOfMessage: true, linked.Token);
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, stopping);
                // Closed by the room: the client may never answer the close, so stop waiting for it.
                if (_closing)
                    await _closed.CancelAsync();
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException)
            {
                socket.Abort();
            }
        }

        public void Dispose()
        {
            _overflow.Dispose();
            _closed.Dispose();
        }
    }
}

/// <summary>Counts a connection's messages in one-second windows.</summary>
internal sealed class MessageRate(int limitPerSecond)
{
    private long _windowStart = Stopwatch.GetTimestamp();
    private int _count;

    /// <summary>Counts one message; true when this second has had more than the limit.</summary>
    public bool Exceeded()
    {
        long now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_windowStart, now) >= TimeSpan.FromSeconds(1))
        {
            _windowStart = now;
            _count = 0;
        }
        return ++_count > limitPerSecond;
    }
}

internal static partial class RelayLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "{Name} joined room {Room} in slot {Slot}")]
    public static partial void Joined(ILogger logger, string name, string room, int slot);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refused {Name} for room {Room}: {Reason}")]
    public static partial void Refused(ILogger logger, string name, string room, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "A connection ended: {Reason}")]
    public static partial void ConnectionEnded(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Room {Room} opened for {Slots} players")]
    public static partial void RoomOpened(ILogger logger, string room, int slots);

    [LoggerMessage(Level = LogLevel.Information, Message = "Room {Room} closed after {Ticks} ticks")]
    public static partial void RoomClosed(ILogger logger, string room, int ticks);
}
