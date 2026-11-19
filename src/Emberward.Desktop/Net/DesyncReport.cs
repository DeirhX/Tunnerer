namespace Emberward.Desktop.Net;

using System.Globalization;
using Emberward.Core;

/// <summary>
/// The file a client writes when the relay finds the players' worlds different: notes for a person, then the match
/// as a <see cref="MatchRecording"/>. The notes are comment lines, so the whole file loads as a recording.
/// </summary>
public static class DesyncReport
{
    public static void Write(
        TextWriter writer, MatchRecording recording, int desyncTick, int slot, string buildId,
        IEnumerable<(int Tick, SyncDigest Digest)> digests, World world)
    {
        var inv = CultureInfo.InvariantCulture;
        writer.WriteLine(string.Create(inv, $"# Emberward desync report: worlds differed at tick {desyncTick}"));
        writer.WriteLine(string.Create(inv, $"# written by slot {slot}, build {buildId}, at tick {world.AdvanceCount}"));
        writer.WriteLine("# Compare this file with another player's. The first tick whose digest differs is where the");
        writer.WriteLine("# worlds went apart, and the part that differs (state, heat, charge or economy) says in what.");
        writer.WriteLine("# MatchRecording.Load reads this file; Replay() rebuilds the match tick by tick.");
        writer.WriteLine("#");
        writer.WriteLine("# digests this client reported: tick state heat charge economy");
        foreach (var (tick, digest) in digests)
            writer.WriteLine(string.Create(inv, $"#   {tick} {digest.State:X16} {digest.Heat:X16} {digest.Charge:X16} {digest.Economy:X16}"));
        writer.WriteLine("#");
        writer.WriteLine(string.Create(inv, $"# tanks at tick {world.AdvanceCount}: slot x y heat health lives"));
        var tanks = world.TankList.Tanks;
        for (int i = 0; i < tanks.Count; i++)
            writer.WriteLine(string.Create(inv,
                $"#   {i} {tanks[i].Position.X} {tanks[i].Position.Y} {tanks[i].Heat:R} {(int)tanks[i].Reactor.Health} {tanks[i].LivesLeft}"));
        writer.WriteLine("#");
        recording.Save(writer);
    }
}
