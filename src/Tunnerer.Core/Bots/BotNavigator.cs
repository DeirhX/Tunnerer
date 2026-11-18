namespace Tunnerer.Core.Bots;

using Tunnerer.Core.Collision;
using Tunnerer.Core.Config;
using Tunnerer.Core.Entities.Tanks;
using Tunnerer.Core.Types;

/// <summary>
/// Where the bot steps next: scores the eight moves against what would stop the tank's hull and a target, strafes away
/// when retreating, and notices when it is stuck or going nowhere. It asks <see cref="World.ProbeTankStep"/> and
/// <see cref="CollisionSolver.TestTankTerrain"/> for every step, the same answers the tank's own move gets, so it
/// never mistakes a gap narrower than the tank for a way through. Shares the bot's random stream, so the order of its
/// draws is part of the bot's behaviour.
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
    private const float ProbeNearWallPenalty = 14f;
    private const float ProbeFarWallPenalty = 8f;
    private const float ProbeSoftPenalty = 1.5f;
    private const float ProbeOpenBonus = 4f;
    private const float KeepHeadingBonus = 2f;
    private const float ReverseHeadingPenalty = 3f;
    /// <summary>A step the bot will not try (rock, a wall, the map edge, another tank): chosen only when all are.</summary>
    private const float ImpassablePenalty = 1000f;

    /// <summary>A step back toward its own base while roaming near it: more than open ground can make up for.</summary>
    private const float OwnBasePenalty = 100f;
    /// <summary>
    /// How near its base centre (along either axis) roaming may not head home: the base, plus more than the longest
    /// plan covers, so a move held for a whole plan cannot end inside.
    /// </summary>
    private const int HomeKeepOutRadius = Tweaks.Base.BaseSize / 2 + 24;
    /// <summary>Ground up to this warm costs a move nothing.</summary>
    private const float WarmGround = 20f;
    /// <summary>What each degree above <see cref="WarmGround"/> on the ground ahead costs a move.</summary>
    private const float HotGroundAversion = 1f;
    /// <summary>Ground this hot cooks the tank in seconds: manoeuvres do not head into it.</summary>
    public const float ScorchingGround = 60f;
    /// <summary>How many steps ahead a move looks for hot ground.</summary>
    private const int HeatLookAhead = 3;
    /// <summary>
    /// What heading into rock adds to a breakout heading's turn (the squared distance between two headings): rock
    /// dead ahead loses to dirt up to a quarter turn aside, since torching through it heats the ground the tank
    /// then stands in.
    /// </summary>
    private const int BreakoutRockTurn = 3;
    /// <summary>In sight of the enemy the bot is not pulled closer than this; inside it, it strafes.</summary>
    private const int CombatStandoff = 12;
    private const float DiagonalStepLength = 1.41421356f;

    // ChooseUnstuckMove step scores.
    private const float UnstuckOpenScore = 20f;
    private const float UnstuckSoftScore = 8f;
    private const float UnstuckPreferredYBonus = 6f;

    /// <summary>With no goal, the bot is going nowhere while it stays within this many cells (along either axis) of one spot.</summary>
    private const int LoiterRadius = 8;
    /// <summary>With a goal, headway is getting this many cells nearer to it than the bot has been before.</summary>
    private const int ProgressStep = 4;

    private readonly BotRandom _rng;
    private Position _lastPosition;
    private bool _hasLastPosition;
    private Position _loiterAnchor;
    private bool _hasLoiterAnchor;
    private int _nearestToGoal;
    private bool _hasNearestToGoal;
    private int _evasiveDir = 1;
    private int _evasiveFramesRemaining;

    public BotNavigator(BotRandom rng) => _rng = rng;

    /// <summary>Consecutive ticks the tank has not moved.</summary>
    public int StuckFrames { get; private set; }

    /// <summary>
    /// Consecutive ticks without headway: toward the goal if the bot has one (it has not come
    /// <see cref="ProgressStep"/> cells nearer than its nearest so far), else away from where it was (it has stayed
    /// within <see cref="LoiterRadius"/> of one spot). Driving up and down a tunnel is not headway.
    /// </summary>
    public int NoProgressFrames { get; private set; }

    /// <summary>Notes where the tank is this tick, and where it is trying to get to (if anywhere).</summary>
    public void UpdateStuckState(Position current, Position? goal)
    {
        if (goal is Position g)
        {
            int distance = Math.Max(Math.Abs(g.X - current.X), Math.Abs(g.Y - current.Y));
            if (!_hasNearestToGoal || distance <= _nearestToGoal - ProgressStep)
            {
                _nearestToGoal = distance;
                _hasNearestToGoal = true;
                NoProgressFrames = 0;
            }
            else
            {
                NoProgressFrames++;
            }
            _hasLoiterAnchor = false;
        }
        else
        {
            if (!_hasLoiterAnchor ||
                Math.Abs(current.X - _loiterAnchor.X) > LoiterRadius || Math.Abs(current.Y - _loiterAnchor.Y) > LoiterRadius)
            {
                _loiterAnchor = current;
                _hasLoiterAnchor = true;
                NoProgressFrames = 0;
            }
            else
            {
                NoProgressFrames++;
            }
            _hasNearestToGoal = false;
        }

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

    /// <summary>Starts the stuck count again: the bot has just changed what it is trying to do.</summary>
    public void ResetStuck() => StuckFrames = 0;

    /// <summary>Starts the no-headway count again from where the tank stands next.</summary>
    public void ResetProgress()
    {
        _hasLoiterAnchor = false;
        _hasNearestToGoal = false;
        NoProgressFrames = 0;
    }

    /// <summary>A move in sight of the enemy: straight at it, or a strafing retreat.</summary>
    public Offset ChooseCombatMove(Tank tank, Position enemy, World world, bool retreat,
        Offset previous, BotBehaviorProfile profile) =>
        retreat
            ? ChooseRetreatEvasiveMove(tank, enemy, world, previous, profile)
            : ChooseBestMove(tank, enemy, world, previous, profile, CombatStandoff);

    /// <summary>
    /// The best-scoring of the eight moves, toward <paramref name="target"/> if there is one. Rock and ore are walls
    /// here: torching them heats the ground the tank then stands in, so only a breakout cuts through them. Within
    /// <paramref name="standoff"/> cells of the target the bot is no longer pulled toward it.
    /// </summary>
    public Offset ChooseBestMove(Tank tank, Position? target, World world, Offset previous,
        BotBehaviorProfile profile, int standoff = 0)
    {
        float bestScore = float.NegativeInfinity;
        Offset best = default;

        foreach (var candidate in MoveCandidates)
        {
            float score = ScoreMove(tank, candidate, target, world, previous, profile, standoff);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// A random-tiebroken step the tank can take (open, or at least dirt), preferring
    /// <paramref name="preferredYDirection"/> and cooler ground. Steps nothing gets through are taken only when every
    /// step is one.
    /// </summary>
    public Offset ChooseUnstuckMove(Tank tank, World world, bool mayTorchRock, int preferredYDirection = 0)
    {
        float bestScore = float.NegativeInfinity;
        Offset best = MoveCandidates[_rng.Next(MoveCandidates.Length)];

        foreach (var candidate in MoveCandidates)
        {
            float score = world.ProbeTankStep(tank, candidate) switch
            {
                StepObstacle.None => UnstuckOpenScore,
                StepObstacle.Dirt => UnstuckSoftScore,
                StepObstacle.Hard when mayTorchRock => 0f,
                _ => -ImpassablePenalty,
            };

            if (preferredYDirection != 0)
                score += candidate.Y == preferredYDirection ? UnstuckPreferredYBonus : 0f;
            score -= HotGroundCost(tank, candidate, world);

            score += (float)_rng.NextDouble();
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// A heading to hold for a while when the bot has been going nowhere: toward <paramref name="target"/> if there is
    /// one, else a random one, turned to the nearest heading that is not walled off or scorching. Rock counts as a
    /// way through only while <paramref name="mayTorchRock"/>, and then as a worse one than dirt a little aside. No
    /// movement (zero) when every heading is walled off or scorching.
    /// </summary>
    public Offset ChooseBreakoutHeading(Tank tank, Position? target, World world, bool mayTorchRock)
    {
        Offset wanted = MoveCandidates[_rng.Next(MoveCandidates.Length)];
        if (target is Position goal && goal != tank.Position)
        {
            int dx = goal.X - tank.Position.X, dy = goal.Y - tank.Position.Y;
            wanted = new Offset(
                Math.Abs(dx) * 2 >= Math.Abs(dy) ? Math.Sign(dx) : 0,
                Math.Abs(dy) * 2 >= Math.Abs(dx) ? Math.Sign(dy) : 0);
        }

        Offset best = wanted;
        int bestTurn = int.MaxValue;
        foreach (var candidate in MoveCandidates)
        {
            var met = world.ProbeTankStep(tank, candidate);
            if (met is StepObstacle.Solid or StepObstacle.Entity || (met == StepObstacle.Hard && !mayTorchRock))
                continue;
            if (world.GroundHeatAt(tank.Position + candidate * HeatLookAhead) >= ScorchingGround)
                continue;
            // Squared distance between two of the eight headings grows with the angle between them.
            int turn = (candidate.X - wanted.X) * (candidate.X - wanted.X) + (candidate.Y - wanted.Y) * (candidate.Y - wanted.Y);
            if (met == StepObstacle.Hard)
                turn += BreakoutRockTurn;
            if (turn < bestTurn)
            {
                bestTurn = turn;
                best = candidate;
            }
        }

        // Walled in on every side: no heading beats standing, so say so and let the caller go on with its plan.
        return bestTurn == int.MaxValue ? default : best;
    }

    /// <summary>What the heat of the ground a few steps along <paramref name="move"/> costs it.</summary>
    private static float HotGroundCost(Tank tank, Offset move, World world) =>
        MathF.Max(0f, world.GroundHeatAt(tank.Position + move * HeatLookAhead) - WarmGround) * HotGroundAversion;

    private float ScoreMove(Tank tank, Offset move, Position? target, World world, Offset previous,
        BotBehaviorProfile profile, int standoff)
    {
        var self = tank.Position;
        var next = self + move;

        // Ground is graded by how much of the hull it leaves in the open: a tunnel a little narrower than the tank is
        // nearly as good as a wide one, and worth more than solid dirt.
        int dir = TankSpriteShape.DirectionOf(move);
        var ground = CollisionSolver.SurveyTankTerrain(world.Terrain, next, dir);
        float score = world.ProbeTankStep(tank, move) switch
        {
            StepObstacle.None or StepObstacle.Dirt =>
                profile.SoftTileBonus + (profile.OpenTileBonus - profile.SoftTileBonus) * ground.OpenShare,
            _ => -ImpassablePenalty,
        };
        score -= HotGroundCost(tank, move, world);

        // Roaming is done away from home: the open floor of its own base would otherwise outscore every tunnel.
        if (tank.Base is { } home)
        {
            int now = Math.Max(Math.Abs(self.X - home.Position.X), Math.Abs(self.Y - home.Position.Y));
            int then = Math.Max(Math.Abs(next.X - home.Position.X), Math.Abs(next.Y - home.Position.Y));
            if (then < now && then <= HomeKeepOutRadius)
                score -= OwnBasePenalty;
        }

        for (int step = ProbeFirstStep; step <= ProbeLastStep; step++)
        {
            var probe = new Position(self.X + move.X * step, self.Y + move.Y * step);
            var ahead = CollisionSolver.SurveyTankTerrain(world.Terrain, probe, dir);
            if (ahead.Worst is StepObstacle.Hard or StepObstacle.Solid)
            {
                score -= step == ProbeFirstStep ? ProbeNearWallPenalty : ProbeFarWallPenalty;
                break;
            }

            score += -ProbeSoftPenalty + (ProbeOpenBonus + ProbeSoftPenalty) * ahead.OpenShare;
        }

        // The pull is the same at any range: the cosine between the move and the way to the target.
        if (target is Position goal)
        {
            float dx = goal.X - self.X, dy = goal.Y - self.Y;
            float distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance > standoff && distance > 0f)
            {
                float stepLength = move.X != 0 && move.Y != 0 ? DiagonalStepLength : 1f;
                score += profile.TargetPull * (move.X * dx + move.Y * dy) / (distance * stepLength);
            }
        }

        if (move.X == previous.X && move.Y == previous.Y)
            score += KeepHeadingBonus;
        else if (move.X == -previous.X && move.Y == -previous.Y)
            score -= ReverseHeadingPenalty;

        score += (float)_rng.NextDouble();
        return score;
    }

    private Offset ChooseRetreatEvasiveMove(Tank tank, Position enemy, World world, Offset previous,
        BotBehaviorProfile profile)
    {
        var self = tank.Position;
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

        return ChooseBestMove(tank, self + composite * EvasiveTargetDistance, world, previous, profile);
    }

    /// <summary>Mixes what the navigator remembers of where it has been and is going, for the sync digest.</summary>
    internal ulong MixState(ulong hash)
    {
        hash = Fnv1a.Mix(Fnv1a.Mix(Fnv1a.Mix(hash, _hasLastPosition), _lastPosition.X), _lastPosition.Y);
        hash = Fnv1a.Mix(Fnv1a.Mix(Fnv1a.Mix(hash, _hasLoiterAnchor), _loiterAnchor.X), _loiterAnchor.Y);
        hash = Fnv1a.Mix(Fnv1a.Mix(hash, _hasNearestToGoal), _nearestToGoal);
        hash = Fnv1a.Mix(Fnv1a.Mix(hash, _evasiveDir), _evasiveFramesRemaining);
        return Fnv1a.Mix(Fnv1a.Mix(hash, StuckFrames), NoProgressFrames);
    }
}
