namespace Emberward.Tests;

using Emberward.Core;
using Emberward.Core.Input;
using Emberward.Core.Rendering;
using Emberward.Core.Types;

internal static class TestSimulation
{
    public static ControllerOutput Idle => default;

    public static ControllerOutput Move(int x, int y) =>
        new() { MoveSpeed = new Offset(x, y) };

    public static ControllerOutput Aim(float x, float y) =>
        new() { AimDirection = new DirectionF(x, y) };

    public static ControllerOutput MoveAndAim(int moveX, int moveY, float aimX, float aimY, bool shootPrimary = false) =>
        new()
        {
            MoveSpeed = new Offset(moveX, moveY),
            AimDirection = new DirectionF(aimX, aimY),
            ShootPrimary = shootPrimary,
        };

    public static void Advance(World world, int frames, ControllerOutput inputForTank0)
    {
        for (int i = 0; i < frames; i++)
            world.Advance(tankIndex => tankIndex == 0 ? inputForTank0 : Idle);
    }

    /// <summary>
    /// Advances <paramref name="frames"/> frames with a per-frame input for tank 0 (others idle). When
    /// <paramref name="captureEvery"/> is positive, every that-many frames is captured to <paramref name="visual"/>.
    /// </summary>
    public static void Advance(
        World world,
        int frames,
        Func<int, ControllerOutput> inputForTank0ByFrame,
        TestVisualTrace? visual = null,
        string phase = "advance",
        int captureEvery = 0)
    {
        for (int i = 0; i < frames; i++)
        {
            var input = inputForTank0ByFrame(i);
            world.Advance(tankIndex => tankIndex == 0 ? input : Idle);
            if (captureEvery > 0 && (i % captureEvery) == 0)
                visual?.Capture(world, $"{phase}_{i:D3}");
        }
    }

    public static void AdvanceIdle(
        World world,
        int frames,
        TestVisualTrace? visual = null,
        string phase = "advance",
        int captureEvery = 0)
    {
        for (int i = 0; i < frames; i++)
        {
            world.Advance(_ => Idle);
            if (captureEvery > 0 && (i % captureEvery) == 0)
                visual?.Capture(world, $"{phase}_{i:D3}");
        }
    }
}
