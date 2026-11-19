namespace Emberward.Tests;

using System.Net;
using System.Net.Sockets;
using Emberward.Core.Types;
using Emberward.Desktop.Net;
using Emberward.Protocol;
using Emberward.Relay;

/// <summary>
/// A launch the relay refuses ends in a <see cref="RelayException"/> carrying the relay's own words; the launch code
/// turns it into the notice of the network menu (<see cref="NetMenuModel"/>), so these are the words a player sees.
/// </summary>
public class NetMenuRefusalTests
{
    private static async Task<(Microsoft.AspNetCore.Builder.WebApplication App, Uri Play)> StartRelayAsync(string secret = "")
    {
        var app = RelayApp.Build(
            ["--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default=Warning", $"--{RelayHub.SecretKey}={secret}"]);
        await app.StartAsync();
        var http = new Uri(app.Urls.Single());
        return (app, new Uri($"ws://127.0.0.1:{http.Port}/play"));
    }

    private static NetLaunchOptions Options(Uri play, string name, string room = "refusals") =>
        new(play, room, name, Players: 2);

    private static MatchConfig Proposal() =>
        NetMatch.ProposeConfig(42, new Size(320, 200), Core.LevelGen.LevelGenMode.Deterministic, players: 2);

    private static NetMenuModel MenuAfter(NetLaunchOptions options, RelayException refusal) =>
        new(new PlayerSettings(), "Ann", new NetMenuRequest(options, refusal.Message));

    [Fact]
    public async Task WrongOrMissingSecret_IsRefusedWithTheReason_AndTheMenuAsksForTheSecret()
    {
        var (app, play) = await StartRelayAsync(secret: "right");
        await using var _ = app;

        foreach (string tried in new[] { "", "wrong" })
        {
            var options = Options(play, "Ann") with { Secret = tried };
            var refused = Assert.Throws<RelayException>(() => NetLaunch.Join(options, Proposal()));

            Assert.Contains("secret", refused.Message, StringComparison.Ordinal);
            var menu = MenuAfter(options, refused);
            Assert.True(menu.SecretRequired);
            Assert.Equal(refused.Message, menu.Notice!.Text);
        }

        using var inside = NetLaunch.Join(Options(play, "Ann") with { Secret = "right" }, Proposal());
        Assert.Equal(0, inside.Slot);
    }

    [Fact]
    public async Task Secret_IsTheSameWithSpacesAround_AndAMissingOneIsToldApartFromAWrongOne()
    {
        // As an environment file or a paste can leave them, on the relay's side and the player's.
        var (app, play) = await StartRelayAsync(secret: " right ");
        await using var _ = app;

        var missing = Assert.Throws<RelayException>(() => NetLaunch.Join(Options(play, "Ann"), Proposal()));
        var wrong = Assert.Throws<RelayException>(() => NetLaunch.Join(Options(play, "Ann") with { Secret = "wrong" }, Proposal()));
        Assert.NotEqual(missing.Message, wrong.Message);
        Assert.Contains(NetMenuModel.SecretRefusalText, missing.Message, StringComparison.Ordinal);
        Assert.Contains(NetMenuModel.SecretRefusalText, wrong.Message, StringComparison.Ordinal);

        using var inside = NetLaunch.Join(Options(play, "Ann") with { Secret = "right\n" }, Proposal());
        Assert.Equal(0, inside.Slot);
    }

    [Fact]
    public async Task FullRoom_IsRefusedWithTheReason_AndTheMenuShowsItWithoutAskingForASecret()
    {
        var (app, play) = await StartRelayAsync();
        await using var _ = app;
        using var ann = NetLaunch.Join(Options(play, "Ann"), Proposal());
        using var bob = NetLaunch.Join(Options(play, "Bob"), Proposal());

        var third = Options(play, "Cy");
        var refused = Assert.Throws<RelayException>(() => NetLaunch.Join(third, Proposal()));

        Assert.Contains("full", refused.Message, StringComparison.Ordinal);
        var menu = MenuAfter(third, refused);
        Assert.False(menu.SecretRequired);
        Assert.Equal(refused.Message, menu.Notice!.Text);
        Assert.Equal("refusals", menu.RoomField.Text);
    }

    [Fact]
    public async Task StartedRoom_IsRefusedToAStranger_WithTheReason()
    {
        var (app, play) = await StartRelayAsync();
        await using var _ = app;
        using var ann = NetLaunch.Join(Options(play, "Ann") with { Players = 2 }, Proposal());
        using var bob = NetLaunch.Join(Options(play, "Bob"), Proposal());
        ann.SendReady(1);
        bob.SendReady(1);
        var limit = System.Diagnostics.Stopwatch.StartNew();
        while (!ann.Started)
        {
            Assert.True(limit.Elapsed < TimeSpan.FromSeconds(10), "The match did not start.");
            Thread.Sleep(5);
            ann.Pump();
            bob.Pump();
        }

        var refused = Assert.Throws<RelayException>(() => NetLaunch.Join(Options(play, "Cy"), Proposal()));

        Assert.Contains("already started", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RelayThatIsNotThere_IsReportedAsUnreachable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await Task.CompletedTask;
        var options = Options(new Uri($"ws://127.0.0.1:{port}/play"), "Ann");

        var refused = Assert.Throws<RelayException>(() => NetLaunch.Join(options, Proposal()));

        Assert.Contains("Could not reach the relay", refused.Message, StringComparison.Ordinal);
        Assert.False(MenuAfter(options, refused).SecretRequired);
    }

    [Fact]
    public async Task ASecondAnn_IsToldWhatTheRelayCalledHer()
    {
        var (app, play) = await StartRelayAsync();
        await using var _ = app;
        using var first = NetLaunch.Join(Options(play, "Ann"), Proposal());
        using var second = NetLaunch.Join(Options(play, "Ann"), Proposal());

        var limit = System.Diagnostics.Stopwatch.StartNew();
        while (second.AssignedName is null)
        {
            Assert.True(limit.Elapsed < TimeSpan.FromSeconds(10), "No roster arrived.");
            Thread.Sleep(5);
            second.Pump();
        }

        Assert.Equal("Ann", second.RequestedName);
        Assert.Equal("Ann (2)", second.AssignedName);
        Assert.Contains("Ann (2)", LobbyText.AssignedNameLine(second.RequestedName, second.AssignedName), StringComparison.Ordinal);
        Assert.Null(LobbyText.AssignedNameLine("Ann", "Ann"));
        Assert.Null(LobbyText.AssignedNameLine(" Ann ", "Ann"));
        Assert.Null(LobbyText.AssignedNameLine("Ann", null));
    }
}
