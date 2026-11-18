namespace Tunnerer.Tests;

using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Tunnerer.Protocol;

public class SnapshotSocketTests
{
    [Fact]
    public async Task FullChunkSplitAcrossWebSocketFragmentsLeavesTheFollowingReportIntact()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
        using var server = await listener.AcceptTcpClientAsync(timeout.Token);
        using var sender = WebSocket.CreateFromStream(client.GetStream(), new WebSocketCreationOptions());
        using var receiver = WebSocket.CreateFromStream(server.GetStream(), new WebSocketCreationOptions { IsServer = true });

        byte[] payload = new byte[SnapshotChunk.MaxBytes];
        new Random(42).NextBytes(payload);
        var chunk = Assert.Single(SnapshotChunk.Split(48, payload));
        byte[] frame = MessageCodec.Encode(chunk);
        Assert.Equal(MessageCodec.MaxFrameBytes, frame.Length);
        await sender.SendAsync(frame.AsMemory(0, 1000), WebSocketMessageType.Binary, false, timeout.Token);
        await sender.SendAsync(frame.AsMemory(1000), WebSocketMessageType.Binary, false, timeout.Token);
        await sender.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Binary, true, timeout.Token);
        var done = new SnapshotDone(48, payload.Length, 123, 456);
        await sender.SendAsync(MessageCodec.Encode(done).AsMemory(), WebSocketMessageType.Binary, true, timeout.Token);

        byte[] buffer = new byte[MessageCodec.MaxFrameBytes];
        foreach (Message expected in new Message[] { chunk, done })
        {
            var received = await MessageFraming.ReadFrameAsync(receiver, buffer, timeout.Token);
            Assert.True(received.HasValue);
            Assert.Equal(expected, MessageCodec.Decode(received.Value));
        }
    }
}
