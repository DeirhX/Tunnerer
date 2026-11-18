namespace Tunnerer.Protocol;

using System.Collections.Immutable;

/// <summary>
/// Everything a client needs to simulate one tick: the encoded input of every slot, and which slots players drive.
/// The relay sends the same bundle to every client, and a client steps tick <see cref="Tick"/> only when it holds it.
/// </summary>
/// <param name="Tick">Zero-based; bundles are numbered without gaps.</param>
/// <param name="Inputs">One encoded input per slot, in slot order.</param>
/// <param name="HumanMask">
/// Bit n is set when a player drives slot n at this tick; a clear bit is a slot a bot drives, which every client
/// runs itself. A client applies a change from the previous bundle's mask before it steps, so a seat changes hands
/// at the same tick everywhere (docs/late-join-design.md §4.2).
/// </param>
public sealed record TickBundle(int Tick, ImmutableArray<uint> Inputs, int HumanMask);
