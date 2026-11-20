namespace Emberward.Protocol;

using System.Collections.Immutable;
using System.Text;

/// <summary>
/// Messages as bytes: one type byte, then the fields in order, little-endian. One message is one WebSocket frame.
/// Decoding checks every length against a limit, so a hostile or broken peer cannot make the reader allocate much.
/// </summary>
public static class MessageCodec
{
    /// <summary>Changes whenever the bytes of any message change. Client and relay must match.</summary>
    public const int ProtocolVersion = 11;

    /// <summary>Maximum size of an ordinary message. Only a snapshot chunk may exceed it.</summary>
    public const int MaxMessageBytes = 4096;

    /// <summary>Chunk kind, tick, index, 16-bit length and compressed bytes.</summary>
    public const int MaxFrameBytes = 1 + 2 * sizeof(int) + sizeof(ushort) + SnapshotChunk.MaxBytes;

    /// <summary>The largest compressed snapshot admitted to a hold.</summary>
    public const int MaxSnapshotBytes = 32 * 1024 * 1024;

    /// <summary>Most ticks one <see cref="Log"/> message carries.</summary>
    public const int MaxLogTicks = 64;

    public const int MaxSlots = 8;
    public const int MinSlots = 2;
    public const int MinTicksPerSecond = 10;
    public const int MaxTicksPerSecond = 120;

    /// <summary>The smallest and largest map side, in cells, a match may ask for.</summary>
    public const int MinMapSide = 64;

    public const int MaxMapSide = 4096;

    /// <summary>Longest name, room or build id, in characters.</summary>
    public const int MaxTextLength = 64;

    /// <summary>Longest shared secret, in characters. The relay refuses to start with a longer one.</summary>
    public const int MaxSecretLength = 256;

    /// <summary>Longest refusal reason, in characters: a sentence or two for the player.</summary>
    public const int MaxReasonLength = 240;

    // A Hello starts with its kind byte and then its protocol number, whatever the protocol. They are the fixed prefix
    // the relay reads first, so that it can tell a client of another protocol to update before it tries to decode a
    // layout it may not know. Refusal, which answers it, must stay as it is for the same reason.
    private const int HelloPrefixLength = 1 + sizeof(int);

    /// <summary>
    /// Reads the protocol number a <see cref="Hello"/> starts with, without decoding the rest. False when the bytes
    /// are not the start of a hello.
    /// </summary>
    public static bool TryReadHelloProtocol(ReadOnlySpan<byte> bytes, out int protocol)
    {
        protocol = 0;
        if (bytes.Length < HelloPrefixLength || (Kind)bytes[0] != Kind.Hello)
            return false;
        protocol = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes[1..]);
        return true;
    }

    public static bool IsSnapshotChunk(ReadOnlySpan<byte> bytes) => !bytes.IsEmpty && (Kind)bytes[0] == Kind.SnapshotChunk;

    private enum Kind : byte
    {
        Hello = 1, Welcome, Roster, Ready, Start, Input, Tick, Hash, Desync, Refusal, StartRequest, Rejoined, Log, SetPaused, PauseState, Ping, Pong, MatchOver,
        ReadyToLoad, SnapshotRequest, SnapshotChunk, SnapshotDone, SnapshotOffer, TakeSeat, Seated, SeatRefused, JoinFailed, JoinHoldState,
        SetClosedToNewcomers, NewcomersState,
    }

    public static byte[] Encode(Message message)
    {
        using var stream = new MemoryStream(64);
        using var w = new BinaryWriter(stream, Encoding.UTF8);
        switch (message)
        {
            case Hello m:
                w.Write((byte)Kind.Hello);
                w.Write(m.Protocol);
                WriteText(w, m.BuildId);
                WriteText(w, m.Name);
                WriteText(w, m.Room);
                WriteConfig(w, m.Config);
                WriteText(w, m.Secret, MaxSecretLength);
                if (m.Snapshots != SnapshotCapabilities.None)
                    w.Write((byte)m.Snapshots);
                break;
            case Welcome m:
                w.Write((byte)Kind.Welcome);
                w.Write((byte)m.Slot);
                WriteConfig(w, m.Config);
                break;
            case Roster m:
                w.Write((byte)Kind.Roster);
                w.Write((byte)m.Names.Length);
                foreach (string name in m.Names)
                    WriteText(w, name);
                break;
            case Ready m:
                w.Write((byte)Kind.Ready);
                w.Write(m.WorldHash);
                break;
            case Start m:
                w.Write((byte)Kind.Start);
                w.Write((byte)m.HumanMask);
                break;
            case StartRequest:
                w.Write((byte)Kind.StartRequest);
                break;
            case Input m:
                w.Write((byte)Kind.Input);
                w.Write(m.Encoded);
                break;
            case Tick m:
                w.Write((byte)Kind.Tick);
                w.Write(m.Bundle.Tick);
                w.Write((byte)m.Bundle.Inputs.Length);
                foreach (uint input in m.Bundle.Inputs)
                    w.Write(input);
                w.Write(checked((byte)m.Bundle.HumanMask));
                break;
            case Hash m:
                w.Write((byte)Kind.Hash);
                w.Write(m.AtTick);
                w.Write(m.Value);
                break;
            case Desync m:
                w.Write((byte)Kind.Desync);
                w.Write(m.AtTick);
                break;
            case Rejoined m:
                w.Write((byte)Kind.Rejoined);
                w.Write((byte)m.HumanMask);
                break;
            case Log m:
                w.Write((byte)Kind.Log);
                w.Write(m.FirstTick);
                w.Write((byte)m.Slots);
                w.Write((ushort)m.Inputs.Length);
                foreach (uint input in m.Inputs)
                    w.Write(input);
                w.Write(m.HumanMasks.AsSpan());
                break;
            case SetPaused m:
                w.Write((byte)Kind.SetPaused);
                w.Write(m.Paused);
                break;
            case PauseState m:
                w.Write((byte)Kind.PauseState);
                w.Write(m.Paused);
                w.Write((byte)m.BySlot);
                break;
            case SetClosedToNewcomers m:
                w.Write((byte)Kind.SetClosedToNewcomers);
                w.Write(m.Closed);
                break;
            case NewcomersState m:
                w.Write((byte)Kind.NewcomersState);
                w.Write(m.Closed);
                break;
            case Ping m:
                w.Write((byte)Kind.Ping);
                w.Write(m.Id);
                break;
            case Pong m:
                w.Write((byte)Kind.Pong);
                w.Write(m.Id);
                break;
            case MatchOver m:
                w.Write((byte)Kind.MatchOver);
                w.Write(m.AtTick);
                break;
            case Refusal m:
                w.Write((byte)Kind.Refusal);
                WriteText(w, m.Reason, MaxReasonLength);
                break;
            case ReadyToLoad:
                w.Write((byte)Kind.ReadyToLoad);
                break;
            case SnapshotRequest m:
                w.Write((byte)Kind.SnapshotRequest);
                w.Write(m.Tick);
                w.Write(m.SendChunks);
                break;
            case SnapshotChunk m:
                if (m.Bytes.IsDefaultOrEmpty || m.Bytes.Length > SnapshotChunk.MaxBytes)
                    throw new ArgumentException("Invalid snapshot chunk length.", nameof(message));
                w.Write((byte)Kind.SnapshotChunk);
                w.Write(m.Tick);
                w.Write(m.Index);
                w.Write((ushort)m.Bytes.Length);
                w.Write(m.Bytes.AsSpan());
                break;
            case SnapshotDone m:
                w.Write((byte)Kind.SnapshotDone);
                WriteSnapshotReport(w, m.Tick, m.Length, m.Digest, m.SyncHash);
                break;
            case SnapshotOffer m:
                w.Write((byte)Kind.SnapshotOffer);
                WriteSnapshotReport(w, m.Tick, m.Length, m.Digest, m.SyncHash);
                break;
            case TakeSeat m:
                w.Write((byte)Kind.TakeSeat);
                w.Write(checked((byte)m.Slot));
                break;
            case Seated m:
                w.Write((byte)Kind.Seated);
                w.Write(checked((byte)m.Slot));
                w.Write(m.AtTick);
                break;
            case SeatRefused m:
                w.Write((byte)Kind.SeatRefused);
                WriteText(w, m.Reason, MaxReasonLength);
                break;
            case JoinHoldState m:
                w.Write((byte)Kind.JoinHoldState);
                WriteText(w, m.Name);
                break;
            case JoinFailed m:
                w.Write((byte)Kind.JoinFailed);
                WriteText(w, m.Reason, MaxReasonLength);
                break;
            default:
                throw new ArgumentException($"No encoding for {message.GetType().Name}.", nameof(message));
        }
        w.Flush();
        return stream.ToArray();
    }

    /// <exception cref="InvalidDataException">The bytes are not one well-formed message.</exception>
    public static Message Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaxFrameBytes ||
            (bytes.Length > MaxMessageBytes && (Kind)bytes[0] != Kind.SnapshotChunk))
            throw new InvalidDataException($"A message of {bytes.Length} bytes is not allowed.");
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var r = new BinaryReader(stream, Encoding.UTF8);
            Message message = (Kind)r.ReadByte() switch
            {
                Kind.Hello => ReadHello(r),
                Kind.Welcome => ReadWelcome(r),
                Kind.Roster => new Roster(ReadList(r, static x => ReadText(x))),
                Kind.Ready => new Ready(r.ReadUInt64()),
                Kind.Start => new Start(r.ReadByte()),
                Kind.StartRequest => new StartRequest(),
                Kind.Input => new Input(r.ReadUInt32()),
                Kind.Tick => ReadTick(r),
                Kind.Hash => new Hash(r.ReadInt32(), r.ReadUInt64()),
                Kind.Desync => new Desync(r.ReadInt32()),
                Kind.Refusal => new Refusal(ReadText(r, MaxReasonLength * 3)),
                Kind.Rejoined => new Rejoined(r.ReadByte()),
                Kind.Log => ReadLog(r),
                Kind.SetPaused => new SetPaused(r.ReadBoolean()),
                Kind.PauseState => new PauseState(r.ReadBoolean(), r.ReadByte()),
                Kind.SetClosedToNewcomers => new SetClosedToNewcomers(r.ReadBoolean()),
                Kind.NewcomersState => new NewcomersState(r.ReadBoolean()),
                Kind.Ping => new Ping(r.ReadUInt32()),
                Kind.Pong => new Pong(r.ReadUInt32()),
                Kind.MatchOver => new MatchOver(r.ReadInt32()),
                Kind.JoinHoldState => new JoinHoldState(ReadText(r)),
                Kind.ReadyToLoad => new ReadyToLoad(),
                Kind.SnapshotRequest => new SnapshotRequest(ReadNonnegative(r), ReadCount(r, 1) != 0),
                Kind.SnapshotChunk => ReadChunk(r),
                Kind.SnapshotDone => new SnapshotDone(ReadNonnegative(r), ReadSnapshotLength(r, 0), r.ReadUInt64(), r.ReadUInt64()),
                Kind.SnapshotOffer => new SnapshotOffer(ReadNonnegative(r), ReadSnapshotLength(r, 1), r.ReadUInt64(), r.ReadUInt64()),
                Kind.TakeSeat => new TakeSeat(ReadCount(r, MaxSlots - 1)),
                Kind.Seated => new Seated(ReadCount(r, MaxSlots - 1), ReadNonnegative(r)),
                Kind.SeatRefused => new SeatRefused(ReadText(r, MaxReasonLength * 3)),
                Kind.JoinFailed => new JoinFailed(ReadText(r, MaxReasonLength * 3)),
                var unknown => throw new InvalidDataException($"Unknown message type {(byte)unknown}."),
            };
            if (stream.Position != stream.Length)
                throw new InvalidDataException("Bytes left over after the message.");
            return message;
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("The message is cut short.", e);
        }
    }

    private static void WriteText(BinaryWriter w, string text, int maxLength = MaxTextLength)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text.Length > maxLength ? text[..maxLength] : text);
        w.Write((ushort)utf8.Length);
        w.Write(utf8);
    }

    private static string ReadText(BinaryReader r, int maxBytes = MaxTextLength * 3)
    {
        int length = r.ReadUInt16();
        if (length > maxBytes)
            throw new InvalidDataException($"A text of {length} bytes is too long.");
        byte[] utf8 = r.ReadBytes(length);
        if (utf8.Length != length)
            throw new EndOfStreamException();
        return Encoding.UTF8.GetString(utf8);
    }

    private static void WriteConfig(BinaryWriter w, MatchConfig c)
    {
        w.Write(c.Seed);
        w.Write((ushort)c.MapWidth);
        w.Write((ushort)c.MapHeight);
        w.Write((byte)c.GenMode);
        w.Write((byte)c.Slots);
        w.Write((byte)c.TicksPerSecond);
        w.Write(c.InputKeyMask);
    }

    private static MatchConfig ReadConfig(BinaryReader r)
    {
        var config = new MatchConfig(
            r.ReadInt32(), r.ReadUInt16(), r.ReadUInt16(), r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadUInt32());
        if (config.Slots is < MinSlots or > MaxSlots)
            throw new InvalidDataException($"A match of {config.Slots} slots is not allowed.");
        if (config.MapWidth is < MinMapSide or > MaxMapSide || config.MapHeight is < MinMapSide or > MaxMapSide)
            throw new InvalidDataException($"A map of {config.MapWidth} by {config.MapHeight} cells is not allowed.");
        if (config.TicksPerSecond is < MinTicksPerSecond or > MaxTicksPerSecond)
            throw new InvalidDataException($"A match at {config.TicksPerSecond} ticks a second is not allowed.");
        return config;
    }

    private static Hello ReadHello(BinaryReader r)
    {
        var hello = new Hello(r.ReadInt32(), ReadText(r), ReadText(r), ReadText(r), ReadConfig(r), ReadText(r, MaxSecretLength * 3));
        return r.BaseStream.Position == r.BaseStream.Length ? hello :
            hello with { Snapshots = (SnapshotCapabilities)ReadCount(r, (int)(SnapshotCapabilities.Upload | SnapshotCapabilities.Join)) };
    }

    private static Welcome ReadWelcome(BinaryReader r)
    {
        int slot = r.ReadSByte();
        var config = ReadConfig(r);
        if (slot < -1 || slot >= config.Slots)
            throw new InvalidDataException("Invalid welcome slot.");
        return new Welcome(slot, config);
    }

    private static int ReadNonnegative(BinaryReader r)
    {
        int value = r.ReadInt32();
        return value >= 0 ? value : throw new InvalidDataException("Expected a nonnegative snapshot field.");
    }

    private static int ReadSnapshotLength(BinaryReader r, int minimum)
    {
        int length = r.ReadInt32();
        if (length < minimum || length > MaxSnapshotBytes)
            throw new InvalidDataException("Invalid compressed snapshot length.");
        return length;
    }

    private static void WriteSnapshotReport(BinaryWriter w, int tick, int length, ulong digest, ulong syncHash)
    {
        w.Write(tick);
        w.Write(length);
        w.Write(digest);
        w.Write(syncHash);
    }

    private static SnapshotChunk ReadChunk(BinaryReader r)
    {
        int tick = ReadNonnegative(r), index = ReadNonnegative(r);
        int length = r.ReadUInt16();
        if (length is 0 or > SnapshotChunk.MaxBytes || length != r.BaseStream.Length - r.BaseStream.Position)
            throw new InvalidDataException("Invalid snapshot chunk length.");
        return new SnapshotChunk(tick, index, [.. r.ReadBytes(length)]);
    }

    private static Tick ReadTick(BinaryReader r)
    {
        int tick = r.ReadInt32();
        var inputs = ReadList(r, static x => x.ReadUInt32());
        return new Tick(new TickBundle(tick, inputs, ReadCount(r, (1 << inputs.Length) - 1)));
    }

    private static Log ReadLog(BinaryReader r)
    {
        int firstTick = r.ReadInt32();
        int slots = r.ReadByte();
        int count = r.ReadUInt16();
        if (slots is < 1 or > MaxSlots || count > MaxLogTicks * slots || count % slots != 0)
            throw new InvalidDataException($"A log of {count} inputs for {slots} slots is not allowed.");
        var inputs = ImmutableArray.CreateBuilder<uint>(count);
        for (int i = 0; i < count; i++)
            inputs.Add(r.ReadUInt32());
        var masks = ImmutableArray.CreateBuilder<byte>(count / slots);
        for (int i = 0; i < count / slots; i++)
            masks.Add((byte)ReadCount(r, (1 << slots) - 1));
        return new Log(firstTick, slots, inputs.MoveToImmutable(), masks.MoveToImmutable());
    }

    private static int ReadCount(BinaryReader r, int max)
    {
        int count = r.ReadByte();
        if (count > max)
            throw new InvalidDataException($"{count} is over the limit of {max}.");
        return count;
    }

    private static ImmutableArray<T> ReadList<T>(BinaryReader r, Func<BinaryReader, T> readItem)
    {
        int count = ReadCount(r, MaxSlots);
        var items = ImmutableArray.CreateBuilder<T>(count);
        for (int i = 0; i < count; i++)
            items.Add(readItem(r));
        return items.MoveToImmutable();
    }
}
