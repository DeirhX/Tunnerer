namespace Tunnerer.Tests;

/// <summary>
/// Tests that depend on real time (sockets, keep-alive timeouts, clocks) run here, on their own, after the tests
/// that run side by side. Beside the simulation tests, which keep every core and thread busy, a timer or a socket
/// answer can be seconds late on a build machine, and a timeout then fires for a connection that is healthy.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimedTestGroup
{
    public const string Name = "Timed";
}
