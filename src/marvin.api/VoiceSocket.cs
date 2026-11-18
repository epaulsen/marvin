using System.Net.WebSockets;
using System.Text.Json;

namespace Marvin.Api;

internal sealed class VoiceSocket(ILogger<VoiceSocket> logger)
{
    private const int MaxAudioBytes = 16_000 * 2 * 60;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    public async Task ReceiveAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var buffer = new byte[4096];
        var control = new byte[1024];
        var controlBytes = 0;
        var audioBytes = 0;
        var recording = false;

        try
        {
            while (true)
            {
                receiveTimeout.CancelAfter(IdleTimeout);
                var result = await socket.ReceiveAsync(buffer.AsMemory(), receiveTimeout.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await CloseAsync(socket, WebSocketCloseStatus.NormalClosure, null);
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    if (!recording)
                    {
                        await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "Expected start before audio.");
                        return;
                    }

                    audioBytes += result.Count;
                    if (audioBytes > MaxAudioBytes)
                    {
                        await CloseAsync(socket, WebSocketCloseStatus.MessageTooBig, "Audio limit exceeded.");
                        return;
                    }

                    continue;
                }

                if (result.Count > control.Length - controlBytes)
                {
                    await CloseAsync(socket, WebSocketCloseStatus.MessageTooBig, "Control message limit exceeded.");
                    return;
                }

                buffer.AsSpan(0, result.Count).CopyTo(control.AsSpan(controlBytes));
                controlBytes += result.Count;
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var command = ReadCommand(control.AsMemory(0, controlBytes));
                controlBytes = 0;
                if (command == "start" && !recording)
                {
                    recording = true;
                }
                else if (command == "end" && recording)
                {
                    logger.LogInformation("Received utterance with {AudioBytes} audio bytes", audioBytes);
                    recording = false;
                    audioBytes = 0;
                }
                else
                {
                    await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "Unexpected control message.");
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (receiveTimeout.IsCancellationRequested)
        {
            logger.LogDebug("Voice WebSocket timed out");
        }
        catch (WebSocketException)
        {
            logger.LogDebug("Voice WebSocket disconnected");
        }
        finally
        {
            if (recording)
            {
                logger.LogInformation("Discarded incomplete utterance with {AudioBytes} audio bytes", audioBytes);
            }
        }
    }

    private static string? ReadCommand(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using var json = JsonDocument.Parse(payload);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                    ? type.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string? reason)
    {
        using var timeout = new CancellationTokenSource(CloseTimeout);
        try
        {
            await socket.CloseOutputAsync(status, reason, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            socket.Abort();
        }
    }
}
