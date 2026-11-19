namespace Emberward.Core.Snapshots;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using static SnapshotLayout;

/// <summary>
/// Reads what <see cref="SnapshotWriter"/> wrote into a freshly built world. An object is filled in place where the
/// built world already holds one of the same type at the same spot (the world itself, its systems, the tanks, the
/// terrain's arrays), so that what the snapshot leaves out (settings, delegates that close over the world) stays
/// wired to it; anything else (a machine planted during play) is created.
/// </summary>
internal sealed class SnapshotReader
{
    private static readonly Dictionary<string, Type> AllowedTypes = SnapshotCoverage.ReadableTypes();
    private static readonly int MaxTypeNameBytes = AllowedTypes.Keys.Max(Encoding.UTF8.GetByteCount);

    // Raw terrain arrays dominate a normal snapshot and are checked in bytes. Bound the graph overhead
    // separately: null references use a single wire byte, and even an empty object needs a table entry.
    private const int MaxObjects = 256 * 1024;
    private const int MaxNonBulkElements = 1024 * 1024;

    private readonly BinaryReader _in;
    private long _collectionBytesLeft;
    private int _nonBulkElementsLeft = MaxNonBulkElements;

    private readonly List<object> _objects = [];
    private readonly HashSet<object> _claimed = new(ReferenceEqualityComparer.Instance);
    private readonly List<Type> _types = [];
    private readonly Queue<object> _pending = new();

    public SnapshotReader(BinaryReader input)
    {
        _in = input;
        _collectionBytesLeft = input.BaseStream.Length;
    }

    /// <summary>Reads a root into <paramref name="existing"/> where it can, then everything it reaches.</summary>
    public object? ReadRoot(Type declared, object? existing)
    {
        object? root = ReadReference(declared, existing);
        while (_pending.Count > 0)
            ReadContents(_pending.Dequeue());
        return root;
    }

    /// <summary>Lets every loaded object that keeps an index rebuild it, in the order the objects were loaded.</summary>
    public void FinishLoading()
    {
        foreach (object loaded in _objects)
            if (loaded is ISnapshotLoaded rebuilds)
                rebuilds.OnSnapshotLoaded();
    }

    private object? ReadValue(Type declared, object? existing)
    {
        if (!IsInline(declared))
            return ReadReference(declared, existing);
        switch (KindOf(declared))
        {
            case Kind.Primitive:
            case Kind.Enum:
                return ReadPrimitive(declared);
            case Kind.TimeSpan:
                return TimeSpan.FromTicks(_in.ReadInt64());
            case Kind.Nullable:
                return _in.ReadBoolean() ? ReadValue(Nullable.GetUnderlyingType(declared)!, existing) : null;
            case Kind.Struct when IsKeptType(declared):
                return existing;
            case Kind.Struct:
                // A boxed copy of what is there, so that fields the snapshot leaves out keep their values.
                object box = existing ?? RuntimeHelpers.GetUninitializedObject(declared);
                ReadFields(declared, box);
                return box;
            default:
                throw new InvalidDataException($"A snapshot cannot hold a {declared}.");
        }
    }

    private object? ReadReference(Type declared, object? existing)
    {
        byte tag = _in.ReadByte();
        switch (tag)
        {
            case RefNull:
                return null;
            case RefSeen:
                return ReadIndexedReference(declared, _objects);
            case RefConstant:
                return ReadIndexedReference(declared, Constants);
            case RefKept:
                if (!IsKeptType(existing?.GetType() ?? declared))
                    throw new InvalidDataException("Only a field retained by the local world can have a kept reference.");
                return existing;
            case RefNew:
                break;
            default:
                throw new InvalidDataException($"Unknown reference tag {tag}.");
        }

        if (_objects.Count >= MaxObjects)
            throw new InvalidDataException("The snapshot contains too many objects.");
        Type type = ReadType();
        if (!declared.IsAssignableFrom(type))
            throw new InvalidDataException($"The snapshot holds a {type} where a {declared} belongs.");
        object value;
        switch (KindOf(type))
        {
            case Kind.String:
                value = ReadString(WorldSnapshot.MaxBytes);
                break;
            case Kind.Array:
                int length = ReadCollectionCount(type.GetElementType()!);
                value = existing is Array same && same.GetType() == type && same.Length == length && !_claimed.Contains(same)
                    ? same
                    : Array.CreateInstance(type.GetElementType()!, length);
                break;
            case Kind.List:
                value = Reuse(existing, type) ?? Activator.CreateInstance(type)!;
                break;
            case Kind.Class:
                value = Reuse(existing, type) ?? RuntimeHelpers.GetUninitializedObject(type);
                break;
            default:
                throw new InvalidDataException($"A snapshot cannot hold a {type}.");
        }
        _objects.Add(value);
        _claimed.Add(value);
        if (value is not string)
            _pending.Enqueue(value);
        return value;
    }

    private object ReadIndexedReference(Type declared, IReadOnlyList<object> table)
    {
        int index = _in.Read7BitEncodedInt();
        if ((uint)index >= (uint)table.Count || !declared.IsInstanceOfType(table[index]))
            throw new InvalidDataException($"Invalid snapshot reference for {declared}.");
        return table[index];
    }

    private int ReadCollectionCount(Type element)
    {
        int count = _in.Read7BitEncodedInt();
        bool bulk = IsBulk(element);
        long bytes = (long)count * (bulk ? BulkSize(element) : 1);
        if (count < 0 || bytes > _in.BaseStream.Length - _in.BaseStream.Position || bytes > _collectionBytesLeft ||
            (!bulk && count > _nonBulkElementsLeft))
            throw new InvalidDataException("The snapshot collection exceeds the available data or element limit.");
        _collectionBytesLeft -= bytes;
        if (!bulk)
            _nonBulkElementsLeft -= count;
        return count;
    }

    private object? Reuse(object? existing, Type type) =>
        existing != null && existing.GetType() == type && !_claimed.Contains(existing) ? existing : null;

    private void ReadContents(object value)
    {
        Type type = value.GetType();
        switch (KindOf(type))
        {
            case Kind.Array:
                ReadArray((Array)value);
                break;
            case Kind.List:
                var list = AsList(value);
                Type element = ListElement(type);
                int count = ReadCollectionCount(element);
                // What the built list held, so that its objects are filled in place where they line up.
                var before = new object?[list.Count];
                list.CopyTo(before, 0);
                list.Clear();
                for (int i = 0; i < count; i++)
                    list.Add(ReadValue(element, i < before.Length ? before[i] : null));
                break;
            default:
                ReadFields(type, value);
                break;
        }
    }

    private void ReadArray(Array array)
    {
        Type element = array.GetType().GetElementType()!;
        if (IsBulk(element))
        {
            if (!BitConverter.IsLittleEndian)
                throw new NotSupportedException("Snapshots are written little-endian.");
            int bytes = array.Length * BulkSize(element);
            _in.BaseStream.ReadExactly(MemoryMarshal.CreateSpan(ref MemoryMarshal.GetArrayDataReference(array), bytes));
            return;
        }
        for (int i = 0; i < array.Length; i++)
            array.SetValue(ReadValue(element, array.GetValue(i)), i);
    }

    private void ReadFields(Type type, object target)
    {
        foreach (var field in FieldsOf(type))
            field.SetValue(target, ReadValue(field.FieldType, field.GetValue(target)));
    }

    private Type ReadType()
    {
        int index = _in.Read7BitEncodedInt();
        if ((uint)index < (uint)_types.Count)
            return _types[index];
        if (index != _types.Count)
            throw new InvalidDataException("Snapshot type table out of order.");
        string name = ReadString(MaxTypeNameBytes);
        if (!AllowedTypes.TryGetValue(name, out Type? type) || _types.Contains(type))
            throw new InvalidDataException("Unknown or repeated snapshot type.");
        _types.Add(type);
        return type;
    }

    private string ReadString(int maxBytes)
    {
        int length = _in.Read7BitEncodedInt();
        if (length < 0 || length > maxBytes || length > _in.BaseStream.Length - _in.BaseStream.Position)
            throw new InvalidDataException("Invalid snapshot string length.");
        return Encoding.UTF8.GetString(_in.ReadBytes(length));
    }

    private object ReadPrimitive(Type type)
    {
        object raw = Type.GetTypeCode(type.IsEnum ? Enum.GetUnderlyingType(type) : type) switch
        {
            TypeCode.Boolean => _in.ReadBoolean(),
            TypeCode.Byte => _in.ReadByte(),
            TypeCode.SByte => _in.ReadSByte(),
            TypeCode.Char => (char)_in.ReadUInt16(),
            TypeCode.Int16 => _in.ReadInt16(),
            TypeCode.UInt16 => _in.ReadUInt16(),
            TypeCode.Int32 => _in.ReadInt32(),
            TypeCode.UInt32 => _in.ReadUInt32(),
            TypeCode.Int64 => _in.ReadInt64(),
            TypeCode.UInt64 => _in.ReadUInt64(),
            TypeCode.Single => BitConverter.Int32BitsToSingle(_in.ReadInt32()),
            TypeCode.Double => BitConverter.Int64BitsToDouble(_in.ReadInt64()),
            _ => throw new InvalidDataException($"A snapshot cannot hold a {type}."),
        };
        return type.IsEnum ? Enum.ToObject(type, raw) : raw;
    }
}
