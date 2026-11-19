using Emberward.Core.Resources;

namespace Emberward.Tests;

public class ResourceTests
{
    [Fact]
    public void Reactor_Pay_DeductsWhenAffordable()
    {
        var r = new Reactor(new ReactorState(100, 50), new ReactorState(200, 100));
        var cost = new ReactorState(30, 10);
        Assert.True(r.Pay(cost));
        Assert.Equal(70, r.Heat);
        Assert.Equal(40, r.Health);
    }

    [Fact]
    public void Reactor_Pay_RefusesWhenUnaffordable()
    {
        var r = new Reactor(new ReactorState(10, 50), new ReactorState(200, 100));
        var cost = new ReactorState(30, 10);
        Assert.False(r.Pay(cost));
        Assert.Equal(10, r.Heat); // unchanged
    }

    [Fact]
    public void Reactor_Add_ClampsToCapacity()
    {
        var r = new Reactor(new ReactorState(180, 90), new ReactorState(200, 100));
        r.Add(new ReactorState(50, 30));
        Assert.Equal(200, r.Heat);
        Assert.Equal(100, r.Health);
    }

    [Fact]
    public void Reactor_Exhaust_ClampsToZero()
    {
        var r = new Reactor(new ReactorState(10, 5), new ReactorState(200, 100));
        bool survived = r.Exhaust(new ReactorState(20, 10));
        Assert.False(survived);
        Assert.Equal(0, r.Heat);
        Assert.Equal(0, r.Health);
    }

    [Fact]
    public void Reactor_Exhaust_ReturnsTrueIfNotNegative()
    {
        var r = new Reactor(new ReactorState(30, 20), new ReactorState(200, 100));
        bool survived = r.Exhaust(new ReactorState(10, 5));
        Assert.True(survived);
        Assert.Equal(20, r.Heat);
        Assert.Equal(15, r.Health);
    }

    [Fact]
    public void Reactor_Absorb_TransfersAndClamps()
    {
        var a = new Reactor(new ReactorState(150, 80), new ReactorState(200, 100));
        var b = new Reactor(new ReactorState(100, 40), new ReactorState(200, 100));
        a.Absorb(b);

        Assert.Equal(200, a.Heat);
        Assert.Equal(100, a.Health);
        Assert.Equal(50, b.Heat);
        Assert.Equal(20, b.Health);
    }

    [Fact]
    public void OreStore_Pay_Works()
    {
        var store = new OreStore(30, 100);
        Assert.True(store.Pay(10));
        Assert.Equal(20, store.Ore);
    }

    [Fact]
    public void OreStore_Pay_RefusesInsufficient()
    {
        var store = new OreStore(9, 100);
        Assert.False(store.Pay(10));
        Assert.Equal(9, store.Ore); // unchanged
    }

    [Fact]
    public void OreStore_Add_ClampsToCapacity_AndSaysHowMuchFit()
    {
        var store = new OreStore(80, 100);
        Assert.Equal(20, store.Add(40));
        Assert.Equal(100, store.Ore);
        Assert.True(store.IsFull);
        Assert.Equal(0, store.Room);
    }

    [Fact]
    public void OreStore_MoveTo_MovesWhatFits_AndConservesOre()
    {
        var a = new OreStore(50, 100);
        var b = new OreStore(90, 100);
        Assert.Equal(10, a.MoveTo(b, 25));
        Assert.Equal(40, a.Ore);
        Assert.Equal(100, b.Ore);
        Assert.Equal(0, a.MoveTo(b, 25));
        Assert.Equal(5, new OreStore(5, 10).MoveTo(new OreStore(0, 10), 25));
    }

    [Fact]
    public void ReactorState_Arithmetic()
    {
        var a = new ReactorState(10, 20);
        var b = new ReactorState(3, 7);
        var sum = a + b;
        var diff = a - b;

        Assert.Equal(13, sum.Heat);
        Assert.Equal(27, sum.Health);
        Assert.Equal(7, diff.Heat);
        Assert.Equal(13, diff.Health);
    }

    [Fact]
    public void ReactorState_IsNegative()
    {
        Assert.False(new ReactorState(1, 1).IsNegative);
        Assert.True(new ReactorState(-1, 1).IsNegative);
        Assert.True(new ReactorState(1, -1).IsNegative);
    }

    [Fact]
    public void ReactorState_TrimNegative()
    {
        var s = new ReactorState(-5, -10);
        s.TrimNegative();
        Assert.Equal(0, s.Heat);
        Assert.Equal(0, s.Health);
    }
}
