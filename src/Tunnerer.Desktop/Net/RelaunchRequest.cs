namespace Tunnerer.Desktop.Net;

/// <summary>
/// How the game wants to be started again when it closes, other than "the same way" (Enter after a match). A change
/// between a local and a network match restarts the process: the renderer cannot be created twice in one.
/// </summary>
/// <param name="Net">The network match to start in; null starts a local one.</param>
/// <param name="OpenNetMenu">Open the network menu at the start (leaving a match, or after a refusal).</param>
/// <param name="Secret">
/// The secret the game was started with, passed on when it starts without a match (leaving one), so that the menu it
/// opens still has it. Empty when there is none, or when the match left was on a relay it does not belong to.
/// </param>
public sealed record RelaunchRequest(NetLaunchOptions? Net = null, bool OpenNetMenu = false, string Secret = "");
