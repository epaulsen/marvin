using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Marvin.Api;

internal sealed class VoiceSocket(
    IVoiceActivityDetectorFactory detectorFactory,
    IVoiceTurnProcessor processor,
    ILogger<VoiceSocket> logger)
{
    private const int WindowBytes = 1024;
    private const int MaxAudioBytes = 16_000 * 2 * 60;
    private const int SilenceWindows = 25;

    public async Task ReceiveAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        using var audio = new MemoryStream();
        var detector = detectorFactory.Create();
        var buffer = new byte[4096];
        var control = new MemoryStream();
        var window = new byte[WindowBytes];
        var windowBytes = 0;
        var silence = 0;
        var started = false;
        var active = false;
        var speech = false;
        CancellationTokenSource? turnCancellation = null;
        Task<byte[]?>? turn = null;
        Task? playback = null;
        Task<ValueWebSocketReceiveResult>? receive = null;

        void ResetAudio()
        {
            audio.SetLength(0);
            windowBytes = 0;
            silence = 0;
            speech = false;
            detector.Reset();
        }

        async Task CancelTurnAsync()
        {
            turnCancellation?.Cancel();
            if (turn is not null)
            {
                _ = turn.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                turn = null;
            }
            if (playback is not null)
            {
                try
                {
                    await playback;
                }
                catch (OperationCanceledException) when (turnCancellation!.IsCancellationRequested)
                {
                }
                playback = null;
            }
            turnCancellation?.Dispose();
            turnCancellation = null;
            ResetAudio();
        }

        try
        {
            await SendTextAsync(socket, """{"type":"hello","audio_out":"pcm","follow_up_ms":0}""", lifetime.Token);

            while (socket.State == WebSocketState.Open)
            {
                receive ??= socket.ReceiveAsync(buffer.AsMemory(), lifetime.Token).AsTask();
                var operation = (Task?)turn ?? playback;
                if (operation is not null && !receive.IsCompleted)
                    await Task.WhenAny(receive, operation);

                if (!receive.IsCompleted && turn is { IsCompleted: true })
                {
                    try
                    {
                        var response = await turn;
                        if (response is { Length: > 0 })
                        {
                            await SendPhaseAsync(socket, "replying", lifetime.Token);
                            playback = SendAudioAsync(socket, response, turnCancellation!.Token);
                        }
                    }
                    catch (OperationCanceledException) when (turnCancellation!.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Voice turn failed");
                        await SendTextAsync(socket, """{"type":"error"}""", lifetime.Token);
                    }
                    turn = null;
                    if (playback is not null)
                        continue;
                    turnCancellation?.Dispose();
                    turnCancellation = null;
                    await SendPhaseAsync(socket, "idle", lifetime.Token);
                    continue;
                }

                if (!receive.IsCompleted && playback is { IsCompleted: true })
                {
                    await playback;
                    playback = null;
                    turnCancellation?.Dispose();
                    turnCancellation = null;
                    await SendPhaseAsync(socket, "idle", lifetime.Token);
                    continue;
                }

                var result = await receive;
                receive = null;
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, lifetime.Token);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    if (control.Length + result.Count > WindowBytes)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "Control message too large.", lifetime.Token);
                        break;
                    }
                    control.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage)
                        continue;

                    var command = ReadCommand(control.ToArray());
                    control.SetLength(0);
                    switch (command)
                    {
                        case "start" when !started:
                            started = true;
                            break;
                        case "wake" when started:
                            await CancelTurnAsync();
                            active = true;
                            break;
                        case "flush" when started:
                            await CancelTurnAsync();
                            break;
                        case "interrupt" when started:
                            await CancelTurnAsync();
                            active = false;
                            await SendPhaseAsync(socket, "idle", lifetime.Token);
                            break;
                        case "ping" when started:
                            break;
                        default:
                            await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Unexpected control message.", lifetime.Token);
                            return;
                    }
                    continue;
                }

                if (!started || !active)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Expected wake before audio.", lifetime.Token);
                    break;
                }
                if (turn is not null || playback is not null)
                    continue;

                var offset = 0;
                while (offset < result.Count)
                {
                    var count = Math.Min(WindowBytes - windowBytes, result.Count - offset);
                    buffer.AsSpan(offset, count).CopyTo(window.AsSpan(windowBytes));
                    windowBytes += count;
                    offset += count;
                    if (windowBytes != WindowBytes)
                        continue;
                    windowBytes = 0;

                    if (audio.Length + WindowBytes > MaxAudioBytes)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "Audio limit exceeded.", lifetime.Token);
                        return;
                    }
                    audio.Write(window);
                    var probability = detector.Evaluate(window);
                    if (probability >= 0.5f)
                    {
                        silence = 0;
                        if (!speech)
                        {
                            speech = true;
                            await SendPhaseAsync(socket, "listening", lifetime.Token);
                        }
                    }
                    else if (speech && probability < 0.35f && ++silence >= SilenceWindows)
                    {
                        var utterance = audio.ToArray();
                        logger.LogInformation("Received utterance with {AudioBytes} audio bytes", utterance.Length);
                        ResetAudio();
                        await SendPhaseAsync(socket, "thinking", lifetime.Token);
                        turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        turn = processor.ProcessAsync(utterance, turnCancellation.Token);
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
            logger.LogDebug("Voice WebSocket disconnected");
        }
        catch (IOException)
        {
            logger.LogDebug("Voice WebSocket disconnected");
        }
        finally
        {
            lifetime.Cancel();
            await CancelTurnAsync();
            control.Dispose();
        }
    }

    private static string? ReadCommand(byte[] payload)
    {
        try
        {
            using var json = JsonDocument.Parse(payload);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Task SendPhaseAsync(WebSocket socket, string phase, CancellationToken cancellationToken) =>
        SendTextAsync(socket, $$"""{"type":"phase","value":"{{phase}}"}""", cancellationToken);

    private static async Task SendAudioAsync(WebSocket socket, byte[] audio, CancellationToken cancellationToken)
    {
        const int frameBytes = 960;
        for (var offset = 0; offset < audio.Length; offset += frameBytes)
        {
            var count = Math.Min(frameBytes, audio.Length - offset);
            await socket.SendAsync(audio.AsMemory(offset, count), WebSocketMessageType.Binary, true, cancellationToken);
            if (offset + count < audio.Length)
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }

    private static Task SendTextAsync(WebSocket socket, string text, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken);
}
