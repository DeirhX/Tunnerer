namespace Tunnerer.Protocol;

using System.Collections.Immutable;

/// <summary>
/// Everything a client needs to simulate one tick: the encoded input of every slot. The relay sends the same bundle
/// to every client, and a client steps tick <see cref="Tick"/> only when it holds it.
/// </summary>
/// <param name="Tick">Zero-based; bundles are numbered without gaps.</param>
/// <param name="Inputs">One encoded input per slot, in slot order.</param>
public sealed record TickBundle(int Tick, ImmutableArray<uint> Inputs);
