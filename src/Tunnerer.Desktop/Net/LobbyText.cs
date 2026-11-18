namespace Tunnerer.Desktop.Net;

/// <summary>Words of the network lobby that need no window to be checked.</summary>
public static class LobbyText
{
    /// <summary>The keys of the lobby that are not about starting the match.</summary>
    public const string KeysLine = "Esc menu   L leave";

    /// <summary>
    /// A line for a player whose name the relay changed (it numbers a second "Ann" as "Ann (2)"), or null when they
    /// have the name they asked for. The assigned name is the one that gets them back into a started match.
    /// </summary>
    public static string? AssignedNameLine(string requested, string? assigned) =>
        assigned is null || string.Equals(assigned, requested.Trim(), StringComparison.Ordinal)
            ? null
            : $"You are {assigned}. Use this name to come back to the match.";
}
