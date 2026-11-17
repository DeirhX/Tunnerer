namespace Tunnerer.Relay;

using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading.Channels;
using Tunnerer.Protocol;

/// <summary>
/// The rooms of this relay and the WebSocket side of each client: reads its messages into its room, writes the
/// room's messages back, and runs each running room's tick clock.
/// </summary>
public sealed class RelayHub(ILogger<RelayHub> logger, IConfiguration configuration)
{
    /// <summary>
    /// The configuration key (and so the environment variable) holding the shared secret. When it is set, a client
    /// must send the same secret to get in; when it is not, the relay is open.
    /// </summary>
    public const string SecretKey = "TUNNERER_RELAY_SECRET";

    private readonly byte[] _secret = System.Text.Encoding.UTF8.GetBytes(configuration[SecretKey] ?? string.Empty);

    public const int MaxRooms = 64;
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

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
        Task writing = peer.WriteAsync(stopping);
        MatchRoom? room = null;
        int slot = -1;
        try
        {
            using var helloLimit = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            helloLimit.CancelAfter(HelloTimeout);
            if (await SocketPeer.ReadAsync(socket, helloLimit.Token) is not Hello hello)
                return;

            (room, slot) = Enter(peer, hello);
            if (room is null)
                return;
            RelayLog.Joined(logger, hello.Name, room.Code, slot);

            while (await SocketPeer.ReadAsync(socket, stopping) is { } message)
            {
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
        if (hello.Protocol != MessageCodec.ProtocolVersion)
        {
            peer.Send(new Refusal(
                $"The relay speaks protocol {MessageCodec.ProtocolVersion}; you speak {hello.Protocol}. Update the game."));
            return (null, -1);
        }
        if (_secret.Length > 0 && !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                _secret, System.Text.Encoding.UTF8.GetBytes(hello.Secret)))
        {
            peer.Send(new Refusal("This relay needs a secret, and the one you sent is not it."));
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

    private void Forget(MatchRoom room)
    {
        lock (_lock)
        {
            if (room.IsEmpty && _rooms.TryGetValue(room.Code, out MatchRoom? known) && known == room)
            {
                _rooms.Remove(room.Code);
                RelayLog.RoomClosed(logger, room.Code, room.TicksClosed);
            }
        }
    }

    private readonly HashSet<MatchRoom> _clocked = [];

    /// <summary>
    /// Runs the room's tick clock until the room is empty. The number of ticks due comes from the match time, so a
    /// late wake-up is made up at once and the rate does not drift.
    /// </summary>
    private void StartClock(MatchRoom room, CancellationToken stopping)
    {
        lock (_lock)
        {
            if (!_clocked.Add(room))
                return;
        }
        _ = Task.Run(async () =>
        {
            // Match time: the time since the start, less the time spent paused.
            double matchSeconds = 0;
            long last = Stopwatch.GetTimestamp();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / room.Config.TicksPerSecond));
            try
            {
                while (!room.IsEmpty && await timer.WaitForNextTickAsync(stopping))
                {
                    long now = Stopwatch.GetTimestamp();
                    if (!room.IsPaused)
                        matchSeconds += Stopwatch.GetElapsedTime(last, now).TotalSeconds;
                    last = now;
                    room.CloseTicksUpTo((int)(matchSeconds * room.Config.TicksPerSecond));
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

        public void Send(Message message)
        {
            if (!_outbox.Writer.TryWrite(MessageCodec.Encode(message)) && !_outbox.Reader.Completion.IsCompleted)
                _overflow.Cancel();
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
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException)
            {
                socket.Abort();
            }
        }

        /// <summary>The client's next message, or null when it closed the connection.</summary>
        public static async Task<Message?> ReadAsync(WebSocket socket, CancellationToken cancel)
        {
            byte[] buffer = new byte[MessageCodec.MaxMessageBytes];
            int length = 0;
            while (true)
            {
                ValueWebSocketReceiveResult part = await socket.ReceiveAsync(buffer.AsMemory(length), cancel);
                if (part.MessageType == WebSocketMessageType.Close)
                    return null;
                length += part.Count;
                if (part.EndOfMessage)
                    return MessageCodec.Decode(buffer.AsSpan(0, length));
                if (length == buffer.Length)
                    throw new InvalidDataException("A message is longer than the protocol allows.");
            }
        }

        public void Dispose() => _overflow.Dispose();
    }
}

internal static partial class RelayLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "{Name} joined room {Room} in slot {Slot}")]
    public static partial void Joined(ILogger logger, string name, string room, int slot);

    [LoggerMessage(Level = LogLevel.Information, Message = "A connection ended: {Reason}")]
    public static partial void ConnectionEnded(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Room {Room} opened for {Slots} players")]
    public static partial void RoomOpened(ILogger logger, string room, int slots);

    [LoggerMessage(Level = LogLevel.Information, Message = "Room {Room} closed after {Ticks} ticks")]
    public static partial void RoomClosed(ILogger logger, string room, int ticks);
}
