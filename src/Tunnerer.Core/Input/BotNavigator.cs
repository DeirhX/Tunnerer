namespace Tunnerer.Core.Input;

using Tunnerer.Core.Terrain;
using Tunnerer.Core.Types;

/// <summary>
/// Where the bot steps next: scores the eight moves against the terrain and a target, strafes away when retreating,
/// and notices when it is stuck. Shares the bot's random stream, so the order of its draws is part of the bot's
/// behaviour.
/// </summary>
internal sealed class BotNavigator
{
    public static readonly Offset[] MoveCandidates =
    {
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
        new(1, 1), new(1, -1), new(-1, 1), new(-1, -1),
    };

    private const int EvasiveSwitchMinFrames = 5;
    private const int EvasiveSwitchMaxFrames = 11;
    /// <summary>How far ahead (in steps along the move) a retreat aims its strafe target.</summary>
    private const int EvasiveTargetDistance = 6;

    // ScoreMove: look-ahead probes 2..4 steps along the move.
    private const int ProbeFirstStep = 2;
    private const int ProbeLastStep = 4;
    private const float ProbeOffMapPenalty = 8f;
    private const float ProbeNearWallPenalty = 14f;
    private const float ProbeFarWallPenalty = 8f;
    private const float ProbeSoftPenalty = 1.5f;
    private const float ProbeOpenBonus = 4f;
    private const float KeepHeadingBonus = 2f;
    private const float ReverseHeadingPenalty = 3f;

    // ChooseUnstuckMove tile scores.
    private const float UnstuckOpenScore = 20f;
    private const float UnstuckSoftScore = 8f;
    private const float UnstuckPreferredYBonus = 6f;

    private readonly Random _rng;
    private Position _lastPosition;
    private bool _hasLastPosition;
    private int _evasiveDir = 1;
    private int _evasiveFramesRemaining;

    public BotNavigator(Random rng) => _rng = rng;

    /// <summary>Consecutive ticks the tank has not moved.</summary>
    public int StuckFrames { get; private set; }

    public void UpdateStuckState(Position current)
    {
        if (!_hasLastPosition)
        {
            _lastPosition = current;
            _hasLastPosition = true;
            StuckFrames = 0;
            return;
        }

        if (current == _lastPosition)
            StuckFrames++;
        else
            StuckFrames = 0;

        _lastPosition = current;
    }

    /// <summary>A move in sight of the enemy: straight at it, or a strafing retreat.</summary>
    public Offset ChooseCombatMove(Position self, Position enemy, ITerrainPixelReadOnly terrain, bool retreat,
        Offset previous, BotBehaviorProfile profile) =>
        retreat
            ? ChooseRetreatEvasiveMove(self, enemy, terrain, previous, profile)
            : ChooseBestMove(self, enemy, terrain, previous, profile);

    /// <summary>The best-scoring of the eight moves, toward <paramref name="target"/> if there is one.</summary>
    public Offset ChooseBestMove(Position self, Position? target, ITerrainPixelReadOnly terrain, Offset previous,
        BotBehaviorProfile profile)
    {
        float bestScore = float.NegativeInfinity;
        Offset best = default;

        foreach (var candidate in MoveCandidates)
        {
            float score = ScoreMove(self, candidate, target, terrain, previous, profile);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>A random-tiebroken step into open (or at least soft) ground, preferring <paramref name="preferredYDirection"/>.</summary>
    public Offset ChooseUnstuckMove(Position self, ITerrainPixelReadOnly terrain, int preferredYDirection = 0)
    {
        float bestScore = float.NegativeInfinity;
        Offset best = MoveCandidates[_rng.Next(MoveCandidates.Length)];

        foreach (var candidate in MoveCandidates)
        {
            var next = self + candidate;
            if (!terrain.IsInside(next))
                continue;

            float score = 0f;
            var pix = terrain.GetPixelRaw(next);
            if (!Pixel.IsAnyCollision(pix))
                score += UnstuckOpenScore;
            else if (!Pixel.IsBlockingCollision(pix))
                score += UnstuckSoftScore;

            if (preferredYDirection != 0)
                score += candidate.Y == preferredYDirection ? UnstuckPreferredYBonus : 0f;

            score += (float)_rng.NextDouble();
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    public static bool NeedsDigging(Position self, Offset move, ITerrainPixelReadOnly terrain)
    {
        var next = self + move;
        if (!terrain.IsInside(next))
            return false;
        return Pixel.IsAnyCollision(terrain.GetPixelRaw(next));
    }

    private float ScoreMove(Position self, Offset move, Position? target, ITerrainPixelReadOnly terrain, Offset previous,
        BotBehaviorProfile profile)
    {
        var next = self + move;
        if (!terrain.IsInside(next))
            return float.NegativeInfinity;

        float score = 0f;
        var nextPix = terrain.GetPixelRaw(next);
        bool anyCollision = Pixel.IsAnyCollision(nextPix);
        bool hardCollision = Pixel.IsBlockingCollision(nextPix);

        if (!anyCollision)
            score += profile.OpenTileBonus; // Prefer already dug/open tunnels for better throughput.
        else if (!hardCollision)
            score += profile.SoftTileBonus; // Soft collision is still acceptable (dirt can be carved).
        else
            score += profile.HardTileBonus; // Hard collision is mostly for deliberate digging profiles.

        for (int step = ProbeFirstStep; step <= ProbeLastStep; step++)
        {
            var probe = new Position(self.X + move.X * step, self.Y + move.Y * step);
            if (!terrain.IsInside(probe))
            {
                score -= ProbeOffMapPenalty;
                break;
            }

            var pix = terrain.GetPixelRaw(probe);
            if (Pixel.IsBlockingCollision(pix))
            {
                score -= step == ProbeFirstStep ? ProbeNearWallPenalty : ProbeFarWallPenalty;
                break;
            }

            score += Pixel.IsAnyCollision(pix) ? -ProbeSoftPenalty : ProbeOpenBonus;
        }

        if (target.HasValue)
        {
            int oldDist = Position.DistanceSquared(self, target.Value);
            int newDist = Position.DistanceSquared(next, target.Value);
            score += (oldDist - newDist) * profile.DistanceWeight;
        }

        if (move.X == previous.X && move.Y == previous.Y)
            score += KeepHeadingBonus;
        else if (move.X == -previous.X && move.Y == -previous.Y)
            score -= ReverseHeadingPenalty;

        score += (float)_rng.NextDouble();
        return score;
    }

    private Offset ChooseRetreatEvasiveMove(Position self, Position enemy, ITerrainPixelReadOnly terrain, Offset previous,
        BotBehaviorProfile profile)
    {
        int awayX = Math.Sign(self.X - enemy.X);
        int awayY = Math.Sign(self.Y - enemy.Y);
        var away = new Offset(awayX, awayY);

        if (_evasiveFramesRemaining <= 0)
        {
            _evasiveDir = _rng.Next(2) == 0 ? -1 : 1;
            _evasiveFramesRemaining = _rng.Next(EvasiveSwitchMinFrames, EvasiveSwitchMaxFrames);
        }
        _evasiveFramesRemaining--;

        var leftPerp = new Offset(-away.Y, away.X);
        var strafe = _evasiveDir > 0 ? leftPerp : new Offset(-leftPerp.X, -leftPerp.Y);

        var composite = new Offset(
            Math.Clamp(away.X + strafe.X, -1, 1),
            Math.Clamp(away.Y + strafe.Y, -1, 1));

        if (composite.X == 0 && composite.Y == 0)
            composite = away.X != 0 || away.Y != 0 ? away : strafe;

        return ChooseBestMove(self, self + composite * EvasiveTargetDistance, terrain, previous, profile);
    }
}
