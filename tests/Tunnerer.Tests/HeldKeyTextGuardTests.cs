namespace Tunnerer.Tests;

using Tunnerer.Desktop.Input;
using static SDL3.SDL;

public class HeldKeyTextGuardTests
{
    [Fact]
    public void Unarmed_TextAndKeysPassThrough()
    {
        var guard = new HeldKeyTextGuard();

        Assert.True(guard.AcceptsText);
        Assert.False(guard.SwallowsKeyDown(Scancode.N));
    }

    [Fact]
    public void TheOpeningKey_IsNotTyped_HoweverLongItIsHeld()
    {
        var guard = new HeldKeyTextGuard();
        guard.Arm(Scancode.N);

        // The typed "n", then the key repeating: each repeat is a key down and another typed "n".
        Assert.False(guard.AcceptsText);
        for (int repeat = 0; repeat < 5; repeat++)
        {
            Assert.True(guard.SwallowsKeyDown(Scancode.N));
            Assert.False(guard.AcceptsText);
        }

        guard.KeyUp(Scancode.N);

        Assert.True(guard.AcceptsText);
        Assert.False(guard.SwallowsKeyDown(Scancode.N));
    }

    [Fact]
    public void AnotherKeyGoingDown_EndsTheGuard()
    {
        var guard = new HeldKeyTextGuard();
        guard.Arm(Scancode.N);

        Assert.False(guard.SwallowsKeyDown(Scancode.A));

        Assert.True(guard.AcceptsText);
    }

    [Fact]
    public void LettingGoOfAnotherKey_LeavesTheGuard_AndClearEndsIt()
    {
        var guard = new HeldKeyTextGuard();
        guard.Arm(Scancode.N);

        guard.KeyUp(Scancode.A);
        Assert.False(guard.AcceptsText);

        guard.Clear();
        Assert.True(guard.AcceptsText);
    }
}
