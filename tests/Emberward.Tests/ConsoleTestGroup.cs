namespace Emberward.Tests;

/// <summary>
/// Tests that capture <see cref="Console.Out"/>, or run code that writes to it, share this collection so xUnit never
/// runs them at the same time; otherwise one test's output lands in another's capture.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ConsoleTestGroup
{
    public const string Name = "Console";
}
