using Emberward.Core;
using Emberward.Core.Entities.Tanks;
using Emberward.Desktop.Gui;

namespace Emberward.Tests;

/// <summary>The reserve's HUD wording (<see cref="ReserveTags"/>), shared by the desktop and the browser.</summary>
public class ReserveTagsTests
{
    private static TankReserveView View(TankReserveStage stage, float toNext = 7.2f, bool canBurn = false,
        bool held = false) =>
        new(true, stage, SecondsLeft: 20f, SecondsToNextStage: toNext, SecondsInStage: 1f, canBurn, SecondsPerBurn: 5f, held);

    [Fact]
    public void EachStage_HasItsTag_AndItsCounter()
    {
        Assert.Null(ReserveTags.Of(default));
        Assert.Null(ReserveTags.Of(View(TankReserveStage.None)));
        Assert.Equal(new ReserveTag("RESERVE LOW  8s", StatusLevel.Warn, false), ReserveTags.Of(View(TankReserveStage.Warning)));
        Assert.Equal(new ReserveTag("DEGRADED  8s", StatusLevel.Bad, false), ReserveTags.Of(View(TankReserveStage.Degraded)));
        Assert.Equal(new ReserveTag("STRANDED  8s", StatusLevel.Bad, true), ReserveTags.Of(View(TankReserveStage.Stranded)));
        Assert.Equal(new ReserveTag("HEAT DEATH", StatusLevel.Bad, true), ReserveTags.Of(View(TankReserveStage.HeatDeath, 0f)));
    }

    [Fact]
    public void AHeldClock_SaysSo_InsteadOfCountingDown()
    {
        Assert.Equal(new ReserveTag("CLOCK HELD", StatusLevel.Ok, false), ReserveTags.Of(View(TankReserveStage.None, held: true)));
        Assert.Equal("DEGRADED  HELD", ReserveTags.Of(View(TankReserveStage.Degraded, held: true))!.Value.Text);
        Assert.False(ReserveTags.Of(View(TankReserveStage.Stranded, held: true))!.Value.Blink);
    }

    [Fact]
    public void TheBurnPrompt_ShowsFromTheWarning_WhileThereIsSomethingToBurn()
    {
        Assert.Null(ReserveTags.BurnPrompt(View(TankReserveStage.None, canBurn: true)));
        Assert.Null(ReserveTags.BurnPrompt(View(TankReserveStage.Degraded, canBurn: false)));
        Assert.Equal("Burn emberite  +5s each", ReserveTags.BurnPrompt(View(TankReserveStage.Warning, canBurn: true)));
    }
}
