namespace Emberward.Core.Snapshots;

using System.Runtime.InteropServices;
using static SnapshotLayout;

/// <summary>
/// Writes the object graph under its roots. An object is written once, where it is first met; later references
/// name it by number. Objects are written breadth first (a reference only queues the object), so a long chain of
/// links does not recurse.
/// </summary>
internal sealed class SnapshotWriter
{
    private readonly BinaryWriter _out;
    private readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, int> _constants = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Type, int> _types = new();
    private readonly Queue<object> _pending = new();
    private readonly Stack<string> _path = new();

    public SnapshotWriter(BinaryWriter output)
    {
        _out = output;
        for (int i = 0; i < Constants.Count; i++)
            _constants.Add(Constants[i], i);
    }

    /// <summary>Writes a root, then every object it reaches that has not been written yet.</summary>
    public void WriteRoot(object root, string name)
    {
        _path.Push(name);
        WriteReference(root);
        while (_pending.Count > 0)
            WriteContents(_pending.Dequeue());
        _path.Pop();
    }

    private void WriteValue(Type declared, object? value)
    {
        if (!IsInline(declared))
        {
            WriteReference(value);
            return;
        }
        switch (KindOf(declared))
        {
            case Kind.Primitive:
            case Kind.Enum:
                WritePrimitive(declared, value!);
                break;
            case Kind.TimeSpan:
                _out.Write(((TimeSpan)value!).Ticks);
                break;
            case Kind.Nullable:
                _out.Write(value != null);
                if (value != null)
                    WriteValue(Nullable.GetUnderlyingType(declared)!, value);
                break;
            case Kind.Struct:
                if (!IsKeptType(declared)) // a struct marked [NotSaved] is left as the loading world has it
                    WriteFields(declared, value!);
                break;
            default:
                throw Unsupported(declared);
        }
    }

    private void WriteReference(object? value)
    {
        if (value == null)
        {
            _out.Write(RefNull);
            return;
        }
        if (_ids.TryGetValue(value, out int id))
        {
            _out.Write(RefSeen);
            _out.Write7BitEncodedInt(id);
            return;
        }
        if (_constants.TryGetValue(value, out int constant))
        {
            _out.Write(RefConstant);
            _out.Write7BitEncodedInt(constant);
            return;
        }
        Type type = value.GetType();
        if (IsKeptType(type))
        {
            _out.Write(RefKept);
            return;
        }
        Kind kind = KindOf(type);
        if (kind is not (Kind.String or Kind.Array or Kind.List or Kind.Class))
            throw Unsupported(type);

        _ids.Add(value, _ids.Count);
        _out.Write(RefNew);
        WriteType(type);
        switch (value)
        {
            case string text:
                _out.Write(text);
                return; // immutable: nothing to queue
            case Array array:
                _out.Write7BitEncodedInt(array.Length);
                break;
        }
        _pending.Enqueue(value);
    }

    private void WriteContents(object value)
    {
        Type type = value.GetType();
        switch (KindOf(type))
        {
            case Kind.Array:
                WriteArray((Array)value);
                break;
            case Kind.List:
                var list = AsList(value);
                Type element = ListElement(type);
                _out.Write7BitEncodedInt(list.Count);
                for (int i = 0; i < list.Count; i++)
                    WriteValue(element, list[i]);
                break;
            default:
                WriteFields(type, value);
                break;
        }
    }

    private void WriteArray(Array array)
    {
        Type element = array.GetType().GetElementType()!;
        if (IsBulk(element))
        {
            if (!BitConverter.IsLittleEndian)
                throw new NotSupportedException("Snapshots are written little-endian.");
            int bytes = array.Length * BulkSize(element);
            _out.Write(MemoryMarshal.CreateReadOnlySpan(ref MemoryMarshal.GetArrayDataReference(array), bytes));
            return;
        }
        for (int i = 0; i < array.Length; i++)
            WriteValue(element, array.GetValue(i));
    }

    private void WriteFields(Type type, object value)
    {
        foreach (var field in FieldsOf(type))
        {
            _path.Push($"{type.Name}.{field.Name}");
            WriteValue(field.FieldType, field.GetValue(value));
            _path.Pop();
        }
    }

    private void WriteType(Type type)
    {
        if (_types.TryGetValue(type, out int index))
        {
            _out.Write7BitEncodedInt(index);
            return;
        }
        index = _types.Count;
        _types.Add(type, index);
        _out.Write7BitEncodedInt(index);
        _out.Write(type.AssemblyQualifiedName!);
    }

    private void WritePrimitive(Type type, object value)
    {
        switch (Type.GetTypeCode(type.IsEnum ? Enum.GetUnderlyingType(type) : type))
        {
            case TypeCode.Boolean: _out.Write((bool)value); break;
            case TypeCode.Byte: _out.Write(Convert.ToByte(value, null)); break;
            case TypeCode.SByte: _out.Write(Convert.ToSByte(value, null)); break;
            case TypeCode.Char: _out.Write((ushort)(char)value); break;
            case TypeCode.Int16: _out.Write(Convert.ToInt16(value, null)); break;
            case TypeCode.UInt16: _out.Write(Convert.ToUInt16(value, null)); break;
            case TypeCode.Int32: _out.Write(Convert.ToInt32(value, null)); break;
            case TypeCode.UInt32: _out.Write(Convert.ToUInt32(value, null)); break;
            case TypeCode.Int64: _out.Write(Convert.ToInt64(value, null)); break;
            case TypeCode.UInt64: _out.Write(Convert.ToUInt64(value, null)); break;
            case TypeCode.Single: _out.Write(BitConverter.SingleToInt32Bits((float)value)); break;
            case TypeCode.Double: _out.Write(BitConverter.DoubleToInt64Bits((double)value)); break;
            default: throw Unsupported(type);
        }
    }

    private NotSupportedException Unsupported(Type type) => new(
        $"A snapshot cannot save a {type} (at {string.Join(" > ", _path.Reverse())}). Save it as a supported type, " +
        "or mark the field [NotSaved] with the reason (rebuilt on load, fixed for the match, output only, scratch).");
}
