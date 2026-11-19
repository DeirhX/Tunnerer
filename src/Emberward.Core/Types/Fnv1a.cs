namespace Emberward.Core.Types;

/// <summary>
/// The 64-bit FNV-1a hash, fed 32-bit words: what the world, rules and economy hashes are built from. The same on
/// every machine and run, unlike <see cref="object.GetHashCode"/>.
/// </summary>
public static class Fnv1a
{
    public const ulong Offset = 1469598103934665603UL;
    public const ulong Prime = 1099511628211UL;

    public static ulong Mix(ulong hash, uint value) => (hash ^ value) * Prime;
}
