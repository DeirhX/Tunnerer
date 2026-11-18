namespace Tunnerer.Core.Snapshots;

/// <summary>
/// Leaves a field, or every value of a type, out of a <see cref="WorldSnapshot"/>. Everything else reachable from
/// the world is saved, so a mark is the only way to leave something out, and it must say why: rebuilt on load from
/// what, fixed for the match by the settings, output only, or scratch that a step fills before it reads it. Loading
/// keeps whatever the freshly built world holds there. <c>SnapshotCoverageTests</c> lists every mark.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class NotSavedAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}

/// <summary>
/// An object that rebuilds what it keeps out of the snapshot (an index, a lookup) once the whole snapshot is loaded.
/// Called in the order the objects were loaded.
/// </summary>
internal interface ISnapshotLoaded
{
    void OnSnapshotLoaded();
}
