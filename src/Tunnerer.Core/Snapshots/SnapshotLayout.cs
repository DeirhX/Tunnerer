namespace Tunnerer.Core.Snapshots;

using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Tunnerer.Core.Entities.Machines;

/// <summary>
/// What the snapshot writer and reader agree on: which fields of a type are saved and in what order, how each kind of
/// value is written, and the shared objects that are written as an index instead of their contents.
/// </summary>
internal static class SnapshotLayout
{
    /// <summary>Opens and closes every snapshot, so that a truncated or foreign stream fails at once.</summary>
    public const uint Magic = 0x504E5354; // "TSNP"

    // How a reference is written.
    public const byte RefNull = 0;
    public const byte RefNew = 1;
    public const byte RefSeen = 2;
    public const byte RefKept = 3;     // a class marked [NotSaved]: the loading world keeps its own
    public const byte RefConstant = 4; // an entry of Constants

    /// <summary>
    /// Immutable objects shared by every world in the process (one per machine type), written as their index here so
    /// that a loaded machine points at the same behaviour as a built one.
    /// </summary>
    public static readonly IReadOnlyList<object> Constants = [.. MachineBehaviors.All];

    private static readonly ConcurrentDictionary<Type, FieldInfo[]> FieldCache = new();

    public enum Kind
    {
        Primitive,
        Enum,
        String,
        TimeSpan,
        Nullable,
        Struct,
        Array,
        List,
        Class,
        Unsupported,
    }

    /// <summary>
    /// The saved instance fields of <paramref name="type"/>: the base type's first, then the type's own in declaration
    /// order (metadata order, which is fixed for a build). Fields marked <see cref="NotSavedAttribute"/> are left out.
    /// </summary>
    public static FieldInfo[] FieldsOf(Type type) => FieldCache.GetOrAdd(type, CollectFields);

    // The walk meets types at run time, which no annotation can name, so a trimmed build (the browser) cannot prove
    // their fields are kept. No trimmed build saves or loads a snapshot yet: the relay does not yet request snapshots. Before
    // browser players join matches, keep Core's fields in that build or replace this walk with a source generator
    // (docs/late-join-design.md §13, step 6).
    [UnconditionalSuppressMessage("Trimming", "IL2075:UnrecognizedReflectionPattern",
        Justification = "No trimmed build saves or loads a snapshot yet; see docs/late-join-design.md §13, step 6.")]
    private static FieldInfo[] CollectFields(Type t)
    {
        var chain = new List<Type>();
        for (Type? at = t; at != null && at != typeof(object) && at != typeof(ValueType); at = at.BaseType)
            chain.Add(at);
        chain.Reverse();
        var fields = new List<FieldInfo>();
        foreach (Type declaring in chain)
            fields.AddRange(declaring
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(f => f.GetCustomAttribute<NotSavedAttribute>() == null)
                .OrderBy(f => f.MetadataToken));
        return [.. fields];
    }

    /// <summary>A type whose values are left out wherever they are (<see cref="NotSavedAttribute"/> on the type).</summary>
    public static bool IsKeptType(Type type) => type.GetCustomAttribute<NotSavedAttribute>(inherit: false) != null;

    public static bool IsOurs(Type type) => type.Assembly == typeof(SnapshotLayout).Assembly;

    public static Kind KindOf(Type type)
    {
        if (type.IsEnum)
            return Kind.Enum;
        if (type.IsPrimitive)
            return type == typeof(IntPtr) || type == typeof(UIntPtr) ? Kind.Unsupported : Kind.Primitive;
        if (type == typeof(string))
            return Kind.String;
        if (type == typeof(TimeSpan))
            return Kind.TimeSpan;
        if (Nullable.GetUnderlyingType(type) != null)
            return Kind.Nullable;
        if (type.IsArray)
            return type.GetArrayRank() == 1 ? Kind.Array : Kind.Unsupported;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            return Kind.List;
        if (type.IsValueType)
            return IsOurs(type) || IsValueTuple(type) ? Kind.Struct : Kind.Unsupported;
        if (IsOurs(type) && !typeof(Delegate).IsAssignableFrom(type))
            return Kind.Class;
        return Kind.Unsupported;
    }

    /// <summary>Whether values of this kind sit in a field as themselves (not a reference to an object).</summary>
    public static bool IsInline(Type type) => type.IsValueType;

    /// <summary>Element types whose arrays are copied as raw bytes.</summary>
    public static bool IsBulk(Type elementType) => elementType.IsPrimitive || elementType.IsEnum;

    public static int BulkSize(Type elementType) =>
        Type.GetTypeCode(elementType.IsEnum ? Enum.GetUnderlyingType(elementType) : elementType) switch
        {
            TypeCode.Boolean or TypeCode.Byte or TypeCode.SByte => 1,
            TypeCode.Char or TypeCode.Int16 or TypeCode.UInt16 => 2,
            TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Single => 4,
            TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Double => 8,
            _ => throw new NotSupportedException($"No raw size for {elementType}."),
        };

    public static Type ListElement(Type listType) => listType.GetGenericArguments()[0];

    public static IList AsList(object list) => (IList)list;

    private static bool IsValueTuple(Type type) =>
        type.IsGenericType && type.FullName!.StartsWith("System.ValueTuple`", StringComparison.Ordinal);
}
