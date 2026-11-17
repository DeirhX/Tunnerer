namespace Tunnerer.Protocol;

using System.Collections.Immutable;
using System.Text;

/// <summary>
/// Messages as bytes: one type byte, then the fields in order, little-endian. One message is one WebSocket frame.
/// Decoding checks every length against a limit, so a hostile or broken peer cannot make the reader allocate much.
/// </summary>
public static class MessageCodec
{
    /// <summary>Changes whenever the bytes of any message change. Client and relay must match.</summary>
    public const int ProtocolVersion = 3;

    /// <summary>No message is longer than this; a longer frame is an error.</summary>
    public const int MaxMessageBytes = 4096;

    /// <summary>Most ticks one <see cref="Log"/> message carries.</summary>
    public const int MaxLogTicks = 64;

    public const int MaxSlots = 8;
    public const int MaxTicksPerSecond = 120;
    /// <summary>Longest name, room or build id, in characters.</summary>
    public const int MaxTextLength = 64;

    /// <summary>Longest refusal reason, in characters: a sentence or two for the player.</summary>
    public const int MaxReasonLength = 240;

    private enum Kind : byte
    {
        Hello = 1, Welcome, Roster, Ready, Start, Input, Tick, Hash, Desync, Refusal, StartRequest, Rejoined, Log, SetPaused, PauseState, Ping, Pong,
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
                WriteText(w, m.Secret);
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
            case Ping m:
                w.Write((byte)Kind.Ping);
                w.Write(m.Id);
                break;
            case Pong m:
                w.Write((byte)Kind.Pong);
                w.Write(m.Id);
                break;
            case Refusal m:
                w.Write((byte)Kind.Refusal);
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
        if (bytes.Length is 0 or > MaxMessageBytes)
            throw new InvalidDataException($"A message of {bytes.Length} bytes is not allowed.");
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var r = new BinaryReader(stream, Encoding.UTF8);
            Message message = (Kind)r.ReadByte() switch
            {
                Kind.Hello => new Hello(r.ReadInt32(), ReadText(r), ReadText(r), ReadText(r), ReadConfig(r), ReadText(r)),
                Kind.Welcome => new Welcome(ReadCount(r, MaxSlots - 1), ReadConfig(r)),
                Kind.Roster => new Roster(ReadList(r, ReadText)),
                Kind.Ready => new Ready(r.ReadUInt64()),
                Kind.Start => new Start(r.ReadByte()),
                Kind.StartRequest => new StartRequest(),
                Kind.Input => new Input(r.ReadUInt32()),
                Kind.Tick => new Tick(new TickBundle(r.ReadInt32(), ReadList(r, static x => x.ReadUInt32()))),
                Kind.Hash => new Hash(r.ReadInt32(), r.ReadUInt64()),
                Kind.Desync => new Desync(r.ReadInt32()),
                Kind.Refusal => new Refusal(ReadText(r)),
                Kind.Rejoined => new Rejoined(r.ReadByte()),
                Kind.Log => ReadLog(r),
                Kind.SetPaused => new SetPaused(r.ReadBoolean()),
                Kind.PauseState => new PauseState(r.ReadBoolean(), r.ReadByte()),
                Kind.Ping => new Ping(r.ReadUInt32()),
                Kind.Pong => new Pong(r.ReadUInt32()),
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

    private static string ReadText(BinaryReader r)
    {
        int length = r.ReadUInt16();
        if (length > MaxReasonLength * 3)
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
        if (config.Slots is < 1 or > MaxSlots)
            throw new InvalidDataException($"A match of {config.Slots} slots is not allowed.");
        if (config.TicksPerSecond is < 1 or > MaxTicksPerSecond)
            throw new InvalidDataException($"A match at {config.TicksPerSecond} ticks a second is not allowed.");
        return config;
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
        return new Log(firstTick, slots, inputs.MoveToImmutable());
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
