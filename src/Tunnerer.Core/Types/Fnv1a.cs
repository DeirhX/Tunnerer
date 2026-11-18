namespace Tunnerer.Core.Types;

/// <summary>
/// The 64-bit FNV-1a hash, fed 32-bit words: what the world, rules and economy hashes are built from. The same on
/// every machine and run, unlike <see cref="object.GetHashCode"/>.
/// </summary>
public static class Fnv1a
{
    public const ulong Offset = 1469598103934665603UL;
    public const ulong Prime = 1099511628211UL;

    public static ulong Mix(ulong hash, uint value) => (hash ^ value) * Prime;

    /// <summary>The int's bits. Without this overload an int would convert to float, and lose its low bits.</summary>
    public static ulong Mix(ulong hash, int value) => Mix(hash, (uint)value);

    public static ulong Mix(ulong hash, bool value) => Mix(hash, value ? 1u : 0u);

    /// <summary>Mixes the float's bits, so values that print alike but differ in the last bit still differ.</summary>
    public static ulong Mix(ulong hash, float value) => Mix(hash, (uint)BitConverter.SingleToInt32Bits(value));

    public static ulong Mix(ulong hash, double value) => Mix(hash, BitConverter.DoubleToUInt64Bits(value));

    public static ulong Mix(ulong hash, ulong value) => Mix(Mix(hash, (uint)value), (uint)(value >> 32));

    public static ulong Mix(ulong hash, TimeSpan value) => Mix(hash, (ulong)value.Ticks);
}
