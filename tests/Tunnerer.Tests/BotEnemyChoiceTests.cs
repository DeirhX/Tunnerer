namespace Tunnerer.Tests;

using Tunnerer.Core.Input;
using Tunnerer.Core.Types;

public class BotEnemyChoiceTests
{
    [Fact]
    public void WithTwoTanks_EachPicksTheOther()
    {
        var tanks = TestHelpers.CreateSeededWorld(new Size(320, 200)).TankList.Tanks;
        Assert.Equal(2, tanks.Count);

        Assert.Same(tanks[0], BotTankAI.PickEnemy(tanks, self: 1));
        Assert.Same(tanks[1], BotTankAI.PickEnemy(tanks, self: 0));
    }

    [Fact]
    public void ALoneTank_HasNoEnemy()
    {
        var tanks = TestHelpers.CreateSeededWorld(new Size(320, 200)).TankList.Tanks;

        Assert.Null(BotTankAI.PickEnemy([tanks[0]], self: 0));
    }
}
