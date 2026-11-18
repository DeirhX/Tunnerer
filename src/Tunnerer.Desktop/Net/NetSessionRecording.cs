namespace Tunnerer.Desktop.Net;

using Tunnerer.Protocol;

/// <summary>
/// A session's recording, kept on the desktop beside <see cref="MatchRecording"/>: <see cref="NetSession"/> is shared
/// with the browser, which writes no recordings.
/// </summary>
public static class NetSessionRecording
{
    /// <summary>
    /// What the session has played, as a recording: from the start of the match, or from the snapshot a joiner
    /// loaded. With the settings that is the whole match from there, so replaying it rebuilds this world.
    /// </summary>
    public static MatchRecording Recording(this NetSession session, MatchConfig config)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new(config, session.StartMask, session.Played, session.StartSnapshot);
    }
}
