namespace Tunnerer.Core.Snapshots;

using System.Reflection;
using static SnapshotLayout;

/// <summary>
/// What <see cref="WorldSnapshot"/> leaves out and what it could not write, for <c>SnapshotCoverageTests</c>: the
/// writer only fails on values it meets, so a field that happens to be null in every test world would otherwise go
/// unchecked until a match fills it.
/// </summary>
public static class SnapshotCoverage
{
    /// <summary>
    /// Every <see cref="NotSavedAttribute"/> in Core, as "Type" for a type and "Type.field" for a field (a property's
    /// backing field by the property's name), with its reason, in name order.
    /// </summary>
    public static IReadOnlyList<(string Name, string Reason)> Marks()
    {
        var marks = new List<(string, string)>();
        foreach (Type type in typeof(SnapshotCoverage).Assembly.GetTypes())
        {
            if (type.GetCustomAttribute<NotSavedAttribute>(inherit: false) is { } typeMark)
                marks.Add((NameOf(type), typeMark.Reason));
            foreach (var field in type.GetFields(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (field.GetCustomAttribute<NotSavedAttribute>() is { } fieldMark)
                    marks.Add(($"{NameOf(type)}.{FieldName(field)}", fieldMark.Reason));
        }
        return [.. marks.OrderBy(m => m.Item1, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Walks every type a snapshot of <paramref name="roots"/> can reach (field types, element types, and every Core
    /// type that can stand in a field of an abstract or interface type) and returns each saved field whose type the
    /// writer cannot write, as "Type.field: field type". A type marked <see cref="NotSavedAttribute"/>, and a shared
    /// constant's type (<see cref="MachineBehavior"/>), is not walked into.
    /// <para>
    /// A field declared <see cref="IReadOnlyList{T}"/> passes as a <see cref="List{T}"/>: the writer saves a value by
    /// its runtime type, and such a field holds a list another object owns (<c>TankList.Tanks</c>). Saving a world
    /// that fills it checks the rest.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> UnsavableFields(IEnumerable<Type> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        Type[] coreTypes = typeof(SnapshotCoverage).Assembly.GetTypes();
        var constantTypes = Constants.Select(c => c.GetType()).ToHashSet();
        var seen = new HashSet<Type>();
        var pending = new Queue<Type>(roots);
        var problems = new List<string>();

        void Reach(Type type)
        {
            if (seen.Add(type))
                pending.Enqueue(type);
        }

        while (pending.Count > 0)
        {
            Type type = pending.Dequeue();
            if (IsKeptType(type) || constantTypes.Contains(type))
                continue;
            switch (KindOf(type))
            {
                case Kind.Nullable:
                    Reach(Nullable.GetUnderlyingType(type)!);
                    continue;
                case Kind.Array:
                    Reach(type.GetElementType()!);
                    continue;
                case Kind.List:
                    Reach(ListElement(type));
                    continue;
                case Kind.Struct:
                case Kind.Class:
                    break;
                default:
                    continue; // a leaf, or reported at the field that holds it
            }
            if (type.IsAbstract || type.IsInterface)
                foreach (Type concrete in coreTypes.Where(t => !t.IsAbstract && !t.IsInterface && type.IsAssignableFrom(t)))
                    Reach(concrete);
            if (type.IsInterface)
                continue;
            foreach (var field in FieldsOf(type))
            {
                Type held = field.FieldType;
                if (held.IsGenericType && held.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
                    held = typeof(List<>).MakeGenericType(held.GetGenericArguments());
                if (KindOf(held) == Kind.Unsupported && !IsKeptType(held))
                    problems.Add($"{NameOf(field.DeclaringType!)}.{FieldName(field)}: {held}");
                else
                    Reach(held);
            }
        }
        return [.. problems.Distinct().OrderBy(p => p, StringComparer.Ordinal)];
    }

    private static string NameOf(Type type)
    {
        string name = type.FullName ?? type.Name;
        const string Root = "Tunnerer.Core.";
        return name.StartsWith(Root, StringComparison.Ordinal) ? name[Root.Length..] : name;
    }

    /// <summary>A field's name, or for a property's backing field (<c>&lt;Name&gt;k__BackingField</c>) the property's.</summary>
    private static string FieldName(FieldInfo field) =>
        field.Name is ['<', .. var rest] && rest.IndexOf('>', StringComparison.Ordinal) is var end and > 0
            ? rest[..end]
            : field.Name;
}
