namespace Tunnerer.Relay;

/// <summary>Builds the relay's web application: <c>/play</c> is the WebSocket endpoint, <c>/health</c> says it is up.</summary>
public static class RelayApp
{
    /// <summary>
    /// A connection that has been silent for this long is pinged, and one that then does not answer within
    /// <see cref="DefaultKeepAliveTimeout"/> is ended, so a client that vanished (its connection to the reverse
    /// proxy still up) frees its slot in under twenty seconds. The pings are WebSocket control frames, answered by
    /// the client's own socket whatever its game loop is doing.
    /// </summary>
    public static readonly TimeSpan DefaultKeepAliveInterval = TimeSpan.FromSeconds(7);

    public static readonly TimeSpan DefaultKeepAliveTimeout = TimeSpan.FromSeconds(10);

    /// <exception cref="InvalidOperationException">The configured secret is longer than the protocol carries.</exception>
    public static WebApplication Build(
        string[] args, TimeSpan? keepAliveInterval = null, TimeSpan? keepAliveTimeout = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        RelayHub.CheckedSecret(builder.Configuration);
        builder.Services.AddSingleton<RelayHub>();
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = keepAliveInterval ?? DefaultKeepAliveInterval,
            KeepAliveTimeout = keepAliveTimeout ?? DefaultKeepAliveTimeout,
        });

        app.MapGet("/health", (RelayHub hub) => $"ok rooms={hub.RoomCount}");
        app.Map("/play", async (HttpContext context, RelayHub hub, IHostApplicationLifetime lifetime) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await hub.ServeAsync(socket, lifetime.ApplicationStopping);
        });
        return app;
    }
}
