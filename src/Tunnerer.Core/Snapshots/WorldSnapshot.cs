namespace Tunnerer.Core.Snapshots;

using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Tunnerer.Core.Bots;

/// <summary>
/// A match's state at the end of a tick, for a player who joins it in progress (docs/late-join-design.md §9). The
/// joiner builds the world from the match settings, as every client does, then loads the snapshot over it: what the
/// settings fix for the whole match is never written.
/// <para>
/// Everything reachable from the world, and from the bots beside it, is saved unless marked
/// <see cref="NotSavedAttribute"/> with a reason, so a field added later is saved without anyone remembering to.
/// The bytes are the build's own (no version: a snapshot is only read by the build that wrote it) and canonical: the
/// same state always gives the same bytes, so two players' snapshots of one tick can be compared byte for byte.
/// </para>
/// <para>
/// Nothing here runs unless a snapshot is asked for: saving and loading add nothing to a tick.
/// </para>
/// </summary>
public static class WorldSnapshot
{
    /// <summary>Saves the world and the bots playing in it (a null entry for a slot no bot plays).</summary>
    public static byte[] Save(World world, IReadOnlyList<BotTankAI?> bots)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(bots);
        using var buffer = new MemoryStream();
        using (var output = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            output.Write(SnapshotLayout.Magic);
            output.Write(world.Terrain.Width);
            output.Write(world.Terrain.Height);
            var writer = new SnapshotWriter(output);
            writer.WriteRoot(world, nameof(World));
            writer.WriteRoot(bots.ToArray(), "bots");
            output.Write(SnapshotLayout.Magic);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Loads a snapshot into <paramref name="world"/>, which must be freshly built from the same match settings, and
    /// returns the bots it holds. Throws <see cref="InvalidDataException"/> on a snapshot that does not fit.
    /// </summary>
    public static BotTankAI?[] Load(World world, byte[] snapshot)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Length > MaxBytes)
            throw new InvalidDataException($"The snapshot exceeds {MaxBytes} bytes.");
        try
        {
            return LoadWorld(world, snapshot);
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("The snapshot is truncated.", e);
        }
        catch (FormatException e)
        {
            throw new InvalidDataException("The snapshot contains an invalid encoded number.", e);
        }
    }

    private static BotTankAI?[] LoadWorld(World world, byte[] snapshot)
    {
        int slots = world.TankList.Count;
        using var input = new BinaryReader(new MemoryStream(snapshot, writable: false));
        if (input.ReadUInt32() != SnapshotLayout.Magic)
            throw new InvalidDataException("Not a snapshot.");
        int width = input.ReadInt32(), height = input.ReadInt32();
        if (width != world.Terrain.Width || height != world.Terrain.Height)
            throw new InvalidDataException(
                $"The snapshot is of a {width} x {height} map; the world is {world.Terrain.Width} x {world.Terrain.Height}.");

        var reader = new SnapshotReader(input);
        if (!ReferenceEquals(reader.ReadRoot(typeof(World), world), world))
            throw new InvalidDataException("The snapshot's world did not load into the world given.");
        if (reader.ReadRoot(typeof(BotTankAI?[]), null) is not BotTankAI?[] bots || bots.Length != slots)
            throw new InvalidDataException("The snapshot needs one bot entry per player slot.");
        if (input.ReadUInt32() != SnapshotLayout.Magic || input.BaseStream.Position != input.BaseStream.Length)
            throw new InvalidDataException("The snapshot does not end where it should.");
        reader.FinishLoading();
        return bots;
    }

    /// <summary>Compresses a snapshot for sending. Compare and hash the uncompressed bytes, which are canonical.</summary>
    public static byte[] Compress(byte[] snapshot, CompressionLevel level = CompressionLevel.Optimal)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var buffer = new MemoryStream();
        using (var brotli = new BrotliStream(buffer, level, leaveOpen: true))
            brotli.Write(snapshot);
        return buffer.ToArray();
    }

    /// <summary>
    /// A 64-bit hash of an uncompressed snapshot, for comparing two players' snapshots of one tick and for a joiner
    /// to check what it received (docs/late-join-design.md §10.3). The uncompressed bytes are canonical; the
    /// compressed ones are not, since the compressor's output may change between runtime versions. The first eight
    /// bytes of SHA-256, which every platform computes alike and in hardware where it can.
    /// </summary>
    public static ulong Digest(ReadOnlySpan<byte> snapshot)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(snapshot, hash);
        return BinaryPrimitives.ReadUInt64LittleEndian(hash);
    }

    /// <summary>The largest snapshot a client accepts (docs/late-join-design.md §9.3).</summary>
    public const int MaxBytes = 32 * 1024 * 1024;

    /// <summary>
    /// Decompresses a snapshot, refusing one that would grow past <paramref name="maxBytes"/>: the bytes come from
    /// another player, and a few kilobytes can be made to decompress to gigabytes.
    /// </summary>
    public static byte[] Decompress(byte[] compressed, int maxBytes = MaxBytes)
    {
        ArgumentNullException.ThrowIfNull(compressed);
        using var brotli = new BrotliStream(new MemoryStream(compressed, writable: false), CompressionMode.Decompress);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = brotli.Read(chunk)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new InvalidDataException($"The snapshot decompresses to more than {maxBytes} bytes.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
