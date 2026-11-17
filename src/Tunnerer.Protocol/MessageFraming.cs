namespace Tunnerer.Protocol;

using System.Net.WebSockets;

/// <summary>How a message travels over a WebSocket, the same for the client and the relay: one binary frame each.</summary>
public static class MessageFraming
{
    /// <summary>
    /// The bytes of the next message, read into <paramref name="buffer"/>, or null when the other side closed the
    /// connection. A message that does not fit the buffer is longer than the protocol allows.
    /// </summary>
    public static async Task<ArraySegment<byte>?> ReadFrameAsync(WebSocket socket, byte[] buffer, CancellationToken cancel)
    {
        int length = 0;
        while (true)
        {
            ValueWebSocketReceiveResult part =
                await socket.ReceiveAsync(buffer.AsMemory(length), cancel).ConfigureAwait(false);
            if (part.MessageType == WebSocketMessageType.Close)
                return null;
            length += part.Count;
            if (part.EndOfMessage)
                return new ArraySegment<byte>(buffer, 0, length);
            if (length == buffer.Length)
                throw new InvalidDataException("A message is longer than the protocol allows.");
        }
    }
}
