namespace Tunnerer.Relay;

/// <summary>Builds the relay's web application: <c>/play</c> is the WebSocket endpoint, <c>/health</c> says it is up.</summary>
public static class RelayApp
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.Services.AddSingleton<RelayHub>();
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

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
