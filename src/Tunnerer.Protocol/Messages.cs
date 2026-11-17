namespace Tunnerer.Protocol;

using System.Collections.Immutable;

/// <summary>
/// The settings every client builds its world from. The first client into a room proposes them; the relay hands the
/// same ones to everyone who joins. The relay does not interpret them beyond <see cref="Slots"/>,
/// <see cref="TicksPerSecond"/> and <see cref="InputKeyMask"/>.
/// </summary>
/// <param name="Slots">How many tanks the match has: the most players it can take.</param>
/// <param name="TicksPerSecond">The simulation rate, which is the rate the relay closes ticks at.</param>
/// <param name="GenMode">The level generator variant; the two make different maps from one seed.</param>
/// <param name="InputKeyMask">
/// The bits of an encoded input that are keys. The relay may skip a queued input that differs from the next only
/// outside this mask (see <see cref="LockstepRoom"/>).
/// </param>
public sealed record MatchConfig(
    int Seed, int MapWidth, int MapHeight, int GenMode, int Slots, int TicksPerSecond, uint InputKeyMask);

/// <summary>One message between a client and the relay. <see cref="MessageCodec"/> turns it into bytes and back.</summary>
public abstract record Message;

/// <summary>
/// Client to relay, first message: join <paramref name="Room"/>, or create it with <paramref name="Config"/>. Its
/// first two fields, the kind byte and <paramref name="Protocol"/>, stay where they are in every protocol.
/// </summary>
/// <param name="Protocol"><see cref="MessageCodec.ProtocolVersion"/> of the sender.</param>
/// <param name="BuildId">Identifies the game build; a room holds clients of one build only.</param>
/// <param name="Config">Used only if the room does not exist yet.</param>
/// <param name="Secret">The relay's shared secret, if it asks for one; empty otherwise.</param>
public sealed record Hello(
    int Protocol, string BuildId, string Name, string Room, MatchConfig Config, string Secret = "") : Message;

/// <summary>Relay to client: you are in, in <paramref name="Slot"/>, and this is the match to build.</summary>
public sealed record Welcome(int Slot, MatchConfig Config) : Message;

/// <summary>Relay to clients: who is in each slot now; an empty name is an empty slot.</summary>
public sealed record Roster(ImmutableArray<string> Names) : Message
{
    public bool Equals(Roster? other) => other is not null && Names.SequenceEqual(other.Names);

    public override int GetHashCode() => Names.Length;
}

/// <summary>Client to relay: my world is built, and this is its hash before the first tick.</summary>
public sealed record Ready(ulong WorldHash) : Message;

/// <summary>
/// Client to relay, from the host (the player in the lowest slot): start now with the players who are here. The
/// match starts as soon as every one of them is ready. A full room starts without being asked.
/// </summary>
public sealed record StartRequest : Message;

/// <summary>Relay to clients: everyone here is ready and agrees; ticks follow.</summary>
/// <param name="HumanMask">Bit n is set when a player drives slot n. The other slots are for bots.</param>
public sealed record Start(int HumanMask) : Message;

/// <summary>
/// Relay to a client that has come back to a running match under the name it had: the match is on, and its ticks
/// so far follow as <see cref="Log"/> messages, then live <see cref="Tick"/>s.
/// </summary>
/// <param name="HumanMask">As in <see cref="Start"/>, from when the match started.</param>
public sealed record Rejoined(int HumanMask) : Message;

/// <summary>Relay to a returning client: a run of past ticks, <paramref name="Slots"/> inputs for each.</summary>
/// <param name="Inputs">Tick <paramref name="FirstTick"/>'s inputs in slot order, then the next tick's, and so on.</param>
public sealed record Log(int FirstTick, int Slots, ImmutableArray<uint> Inputs) : Message
{
    public bool Equals(Log? other) =>
        other is not null && FirstTick == other.FirstTick && Slots == other.Slots && Inputs.SequenceEqual(other.Inputs);

    public override int GetHashCode() => FirstTick;
}

/// <summary>Client to relay: stop (or go on) closing ticks, for everyone.</summary>
public sealed record SetPaused(bool Paused) : Message;

/// <summary>Relay to clients: the match is paused or running again, and which slot asked.</summary>
public sealed record PauseState(bool Paused, int BySlot) : Message;

/// <summary>Client to relay: answer with a <see cref="Pong"/> carrying the same number, to time the round trip.</summary>
public sealed record Ping(uint Id) : Message;

/// <summary>Relay to client: the answer to a <see cref="Ping"/>.</summary>
public sealed record Pong(uint Id) : Message;

/// <summary>Client to relay: the local player's encoded input for one step.</summary>
public sealed record Input(uint Encoded) : Message;

/// <summary>Relay to clients: one tick to simulate.</summary>
public sealed record Tick(TickBundle Bundle) : Message
{
    public bool Equals(Tick? other) =>
        other is not null && Bundle.Tick == other.Bundle.Tick && Bundle.Inputs.SequenceEqual(other.Bundle.Inputs);

    public override int GetHashCode() => Bundle.Tick;
}

/// <summary>Client to relay: the hash of my world after <paramref name="AtTick"/> ticks.</summary>
public sealed record Hash(int AtTick, ulong Value) : Message;

/// <summary>Relay to clients: two of you reported different worlds after <paramref name="AtTick"/> ticks.</summary>
public sealed record Desync(int AtTick) : Message;

/// <summary>Relay to client: the request was refused, or the match cannot go on. The connection closes after it.</summary>
public sealed record Refusal(string Reason) : Message;
