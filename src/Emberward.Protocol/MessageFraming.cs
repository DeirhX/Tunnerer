namespace Emberward.Protocol;

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
        byte[]? overflow = null;
        while (true)
        {
            // A maximum-sized message may still have an empty final fragment. Read one byte in that case
            // to distinguish the terminator from data beyond the limit.
            Memory<byte> target = length == buffer.Length ? (overflow ??= new byte[1]) : buffer.AsMemory(length);
            ValueWebSocketReceiveResult part = await socket.ReceiveAsync(target, cancel).ConfigureAwait(false);
            if (part.MessageType == WebSocketMessageType.Close)
                return null;
            if (part.MessageType != WebSocketMessageType.Binary)
                throw new InvalidDataException("Expected a binary WebSocket message.");
            if (part.Count > buffer.Length - length)
                throw new InvalidDataException("A message is longer than the protocol allows.");
            length += part.Count;
            if (part.EndOfMessage)
                return new ArraySegment<byte>(buffer, 0, length);
        }
    }
}
