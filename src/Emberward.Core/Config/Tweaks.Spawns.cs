namespace Emberward.Core.Config;

using Emberward.Core.Types;

public static partial class Tweaks
{
    /// <summary>
    /// Where level generation puts the bases (<c>LevelGen.EdgeSpawns</c>, DESIGN.md §6.7): every base backs onto the
    /// map's border at the same inset, away from the corners, and the bases are spread around the border with a
    /// seeded jitter, so no side gets a better start and none can tell exactly where the others are.
    /// </summary>
    public static class Spawns
    {
        // Rock between a base's back wall and the map edge, the edge cell included. A tank is 7 cells wide, so this
        // gap seals the back: the gate on the border side opens onto rock and no one can drive behind the base.
        public const int BackRock = 4;

        // From the map edge to the centre of a base.
        public const int EdgeInset = Base.BaseSize / 2 + BackRock;

        // Open ground between a base's side wall and the border it does not back onto, at least, so no base sits in
        // a corner with a second wall at its side. Shrinks on a map too small for it.
        public const int CornerClearance = Base.BaseSize;

        // Rock is cleared from this many cells around a base's three open sides, so every base starts in the same
        // diggable ground and no rock blocks a gate.
        public const int ApronDepth = 12;

        // A base other than the first sits an even share of the border away from the previous one, moved by up to
        // this share of that spacing either way, so its place cannot be read off the first base's.
        public const double JitterShare = 0.3;

        // Bases are at least Base.MinDistance apart, or this share of the even spacing along the border when the map
        // is too small to fit that many bases so far apart.
        public const double MinSpacingShare = 0.6;

        // Balance a layout must reach: the base nearest the map centre is at least this share of the farthest one's
        // distance from it, and (three or more bases) the closest pair of neighbours at least this share of the
        // widest. When no draw reaches both, the most balanced draw is kept.
        public const double MinCentreDistanceRatio = 0.8;
        public const double MinNeighbourDistanceRatio = 0.75;

        // Draws tried before the most balanced one is kept.
        public const int LayoutAttempts = 256;

        // The smallest map a match may use. Smaller maps cannot hold the bases of more than two players without
        // overlap, so the desktop's --size and the browser's clamp both refuse them; the browser's floor is this size.
        public static readonly Size MinMapSize = new(320, 200);
    }
}
