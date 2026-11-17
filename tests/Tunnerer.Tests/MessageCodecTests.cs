namespace Tunnerer.Tests;

using Tunnerer.Protocol;

public class MessageCodecTests
{
    private static readonly MatchConfig Config = new(
        Seed: -12345, MapWidth: 1500, MapHeight: 750, GenMode: 1, Slots: 4, TicksPerSecond: 24, InputKeyMask: 0xFFF);

    public static TheoryData<Message> EveryMessage => new()
    {
        new Hello(MessageCodec.ProtocolVersion, "1.0.0+abc", "Žofie", "lobby", Config, Secret: "s3cret"),
        new Welcome(3, Config),
        new Roster(["Ann", "", "Bob", ""]),
        new Ready(ulong.MaxValue - 1),
        new Start(HumanMask: 0b1011),
        new StartRequest(),
        new Rejoined(HumanMask: 0b0111),
        new Log(FirstTick: 128, Slots: 2, [1u, 2u, 3u, 4u, 5u, 6u]),
        new SetPaused(true),
        new PauseState(Paused: true, BySlot: 3),
        new Ping(77),
        new Pong(77),
        new Input(0x0FFF_FFFF),
        new Tick(new TickBundle(123_456, [1u, 2u, 3u, 4u])),
        new Hash(240, 0xDEADBEEFCAFEF00D),
        new Desync(48),
        new Refusal("This room is full."),
    };

    [Theory]
    [MemberData(nameof(EveryMessage))]
    public void Message_RoundTrips(Message message)
    {
        Assert.Equal(message, MessageCodec.Decode(MessageCodec.Encode(message)));
    }

    [Theory]
    [MemberData(nameof(EveryMessage))]
    public void CutShortMessage_IsRejected(Message message)
    {
        byte[] bytes = MessageCodec.Encode(message);
        if (bytes.Length == 1)
            return; // A message of only its type byte has nothing to cut.

        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(bytes.AsSpan(0, bytes.Length - 1)));
    }

    [Fact]
    public void MalformedBytes_AreRejected()
    {
        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode([]));
        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode([200]));
        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(new byte[MessageCodec.MaxMessageBytes + 1]));
        // A StartRequest with a byte too many.
        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode([.. MessageCodec.Encode(new StartRequest()), 0]));
        // A log whose inputs do not divide into its slots.
        byte[] log = MessageCodec.Encode(new Log(0, 2, [1u, 2u]));
        log[5] = 3;
        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(log));
        // A tick claiming more slots than a match can have.
        byte[] tick = MessageCodec.Encode(new Tick(new TickBundle(1, [1u])));
        tick[5] = MessageCodec.MaxSlots + 1;
        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(tick));
    }

    [Fact]
    public void OverlongText_IsCutToTheLimit()
    {
        var refusal = (Refusal)MessageCodec.Decode(MessageCodec.Encode(new Refusal(new string('x', 500))));

        Assert.Equal(MessageCodec.MaxReasonLength, refusal.Reason.Length);

        var roster = (Roster)MessageCodec.Decode(MessageCodec.Encode(new Roster([new string('x', 500)])));

        Assert.Equal(MessageCodec.MaxTextLength, roster.Names[0].Length);
    }

    [Fact]
    public void FullestLogMessage_FitsInOneMessage()
    {
        var inputs = Enumerable.Repeat(uint.MaxValue, MessageCodec.MaxLogTicks * MessageCodec.MaxSlots).ToArray();

        byte[] bytes = MessageCodec.Encode(new Log(int.MaxValue, MessageCodec.MaxSlots, [.. inputs]));

        Assert.True(bytes.Length <= MessageCodec.MaxMessageBytes);
        Assert.IsType<Log>(MessageCodec.Decode(bytes));
    }

    [Fact]
    public void LongSecret_RoundTripsWhole_AndOnlyAbsurdOnesAreCut()
    {
        string secret = new('k', MessageCodec.MaxSecretLength);
        var hello = new Hello(MessageCodec.ProtocolVersion, "b", "Ann", "room", Config, secret);

        var decoded = (Hello)MessageCodec.Decode(MessageCodec.Encode(hello));

        Assert.Equal(secret, decoded.Secret);
        var tooLong = (Hello)MessageCodec.Decode(MessageCodec.Encode(hello with { Secret = secret + "more" }));
        Assert.Equal(MessageCodec.MaxSecretLength, tooLong.Secret.Length);
    }

    [Theory]
    [InlineData(1, 320, 200, 24)]
    [InlineData(MessageCodec.MaxSlots + 1, 320, 200, 24)]
    [InlineData(2, MessageCodec.MinMapSide - 1, 200, 24)]
    [InlineData(2, 320, MessageCodec.MinMapSide - 1, 24)]
    [InlineData(2, MessageCodec.MaxMapSide + 1, 200, 24)]
    [InlineData(2, 320, MessageCodec.MaxMapSide + 1, 24)]
    [InlineData(2, 320, 200, MessageCodec.MinTicksPerSecond - 1)]
    [InlineData(2, 320, 200, MessageCodec.MaxTicksPerSecond + 1)]
    public void MatchSettingsOutsideTheLimits_AreRejected(int slots, int width, int height, int ticksPerSecond)
    {
        var config = Config with { Slots = slots, MapWidth = width, MapHeight = height, TicksPerSecond = ticksPerSecond };

        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(MessageCodec.Encode(new Welcome(0, config))));
        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(
            MessageCodec.Encode(new Hello(MessageCodec.ProtocolVersion, "b", "Ann", "room", config))));
    }

    [Fact]
    public void MatchSettingsAtTheLimits_AreAccepted()
    {
        var small = Config with
        {
            Slots = MessageCodec.MinSlots,
            MapWidth = MessageCodec.MinMapSide,
            MapHeight = MessageCodec.MinMapSide,
            TicksPerSecond = MessageCodec.MinTicksPerSecond
        };
        var large = Config with
        {
            Slots = MessageCodec.MaxSlots,
            MapWidth = MessageCodec.MaxMapSide,
            MapHeight = MessageCodec.MaxMapSide,
            TicksPerSecond = MessageCodec.MaxTicksPerSecond
        };

        Assert.Equal(new Welcome(0, small), MessageCodec.Decode(MessageCodec.Encode(new Welcome(0, small))));
        Assert.Equal(new Welcome(0, large), MessageCodec.Decode(MessageCodec.Encode(new Welcome(0, large))));
    }

    [Fact]
    public void HelloProtocol_CanBeReadWithoutDecodingTheRest()
    {
        byte[] hello = MessageCodec.Encode(new Hello(7, "b", "Ann", "room", Config));

        Assert.True(MessageCodec.TryReadHelloProtocol(hello, out int protocol));
        Assert.Equal(7, protocol);
        // Only the first five bytes are read: whatever follows may be a layout this build does not know.
        Assert.True(MessageCodec.TryReadHelloProtocol(hello.AsSpan(0, 5), out protocol));
        Assert.Equal(7, protocol);
        Assert.False(MessageCodec.TryReadHelloProtocol(hello.AsSpan(0, 4), out _));
        Assert.False(MessageCodec.TryReadHelloProtocol(MessageCodec.Encode(new Ping(1)), out _));
    }
}
