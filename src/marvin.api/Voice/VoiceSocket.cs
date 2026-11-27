using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

using Marvin.Api.Speech;

namespace Marvin.Api.Voice;

internal sealed class VoiceSocket(
    IVoiceActivityDetectorFactory detectorFactory,
    IVoiceTurnProcessor processor,
    IVoiceSynthesizer synthesizer,
    ILogger<VoiceSocket> logger)
{
    private const int WindowBytes = 1024;
    private const int MaxAudioBytes = 16_000 * 2 * 60;
    private const int SilenceWindows = 25;

    public async Task ReceiveAsync(HttpContext context)
    {
        using var scope = logger.BeginScope("Voice connection {VoiceConnectionId}", context.TraceIdentifier);
        logger.LogInformation("Voice endpoint requested; WebSocket upgrade: {IsWebSocket}",
            context.WebSockets.IsWebSocketRequest);
        if (!context.WebSockets.IsWebSocketRequest)
        {
            logger.LogWarning("Rejecting request without WebSocket upgrade");
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        logger.LogInformation("Voice WebSocket connected");
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
        long receivedAudioBytes = 0;
        long nextAudioLog = 0;
        CancellationTokenSource? turnCancellation = null;
        Task<VoiceTurnResponse?>? turn = null;
        Task? playback = null;
        var requestFollowUp = false;
        Task<ValueWebSocketReceiveResult>? receive = null;

        void ResetAudio()
        {
            audio.SetLength(0);
            windowBytes = 0;
            silence = 0;
            speech = false;
            receivedAudioBytes = 0;
            nextAudioLog = 0;
            detector.Reset();
        }

        async Task CancelTurnAsync()
        {
            if (turn is not null || playback is not null)
                logger.LogDebug("Cancelling active voice processing or playback");
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
            requestFollowUp = false;
            ResetAudio();
        }

        try
        {
            await SendTextAsync(socket, """{"type":"hello","audio_out":"pcm","follow_up_ms":0}""", lifetime.Token);
            logger.LogDebug("Sent hello: PCM output, follow-up disabled");

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
                        logger.LogDebug("Voice processing completed; response audio bytes: {AudioBytes}", response?.Audio.Length ?? 0);
                        if (response is { Audio.Length: > 0 })
                        {
                            await SendPhaseAsync(socket, "replying", lifetime.Token);
                            requestFollowUp = response.RequestFollowUp;
                            playback = SendAudioAsync(socket, response.Audio, turnCancellation!.Token);
                        }
                    }
                    catch (OperationCanceledException) when (turnCancellation!.IsCancellationRequested)
                    {
                        logger.LogDebug("Voice processing cancelled");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Voice turn failed; keeping WebSocket open");
                        try
                        {
                            requestFollowUp = false;
                            var recoveryAudio = await synthesizer.SynthesizeAsync(
                                "Marvin fikk hjernefeil. Vent litt, og sjekk om handlingen allerede ble utført før du prøver igjen.",
                                turnCancellation!.Token);
                            if (recoveryAudio is { Length: > 0 })
                            {
                                await SendPhaseAsync(socket, "replying", lifetime.Token);
                                playback = SendAudioAsync(socket, recoveryAudio, turnCancellation.Token);
                            }
                            else
                            {
                                logger.LogWarning("No recovery speech available; returning Voice PE to idle");
                            }
                        }
                        catch (OperationCanceledException) when (turnCancellation!.IsCancellationRequested)
                        {
                            logger.LogDebug("Recovery speech cancelled");
                        }
                        catch (Exception recoveryException)
                        {
                            logger.LogWarning(recoveryException, "Failed to synthesize recovery speech");
                        }
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
                    logger.LogDebug("Audio playback completed");
                    playback = null;
                    turnCancellation?.Dispose();
                    turnCancellation = null;
                    await SendPhaseAsync(socket, "idle", lifetime.Token);
                    if (requestFollowUp)
                    {
                        await SendTextAsync(socket, """{"type":"request_follow_up"}""", lifetime.Token);
                        logger.LogDebug("Requested follow-up from Voice PE");
                        requestFollowUp = false;
                    }
                    continue;
                }

                var result = await receive;
                receive = null;
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    logger.LogInformation("Voice PE requested WebSocket close: {CloseStatus}", socket.CloseStatus);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, lifetime.Token);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    if (control.Length + result.Count > WindowBytes)
                    {
                        logger.LogWarning("Rejecting oversized control message");
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
                            logger.LogDebug("Received start");
                            started = true;
                            break;
                        case "wake" when started:
                            logger.LogDebug("Received wake");
                            await CancelTurnAsync();
                            active = true;
                            break;
                        case "flush" when started:
                            logger.LogDebug("Received flush");
                            await CancelTurnAsync();
                            break;
                        case "interrupt" when started:
                            logger.LogDebug("Received interrupt");
                            await CancelTurnAsync();
                            active = false;
                            await SendPhaseAsync(socket, "idle", lifetime.Token);
                            break;
                        case "ping" when started:
                            logger.LogDebug("Received ping");
                            break;
                        default:
                            logger.LogWarning("Rejecting malformed or unexpected control message; started: {Started}", started);
                            await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Unexpected control message.", lifetime.Token);
                            return;
                    }
                    continue;
                }

                if (!started || !active)
                {
                    logger.LogWarning("Rejecting audio before start and wake; started: {Started}, active: {Active}", started, active);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Expected wake before audio.", lifetime.Token);
                    break;
                }
                if (turn is not null || playback is not null)
                    continue;

                receivedAudioBytes += result.Count;
                if (receivedAudioBytes >= nextAudioLog)
                {
                    logger.LogDebug("Receiving PCM audio; bytes received in current turn: {AudioBytes}", receivedAudioBytes);
                    nextAudioLog = receivedAudioBytes + 160_000;
                }
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
                        logger.LogWarning("Rejecting audio exceeding the 60-second limit");
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
                            logger.LogDebug("VAD detected speech; probability: {Probability:F3}", probability);
                            await SendPhaseAsync(socket, "listening", lifetime.Token);
                        }
                    }
                    else if (speech && probability < 0.35f && ++silence >= SilenceWindows)
                    {
                        logger.LogDebug("VAD detected end of speech after {SilenceWindows} silence windows", silence);
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
            logger.LogDebug("Voice connection cancelled");
        }
        catch (WebSocketException ex)
        {
            logger.LogDebug(ex, "Voice WebSocket disconnected");
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Voice WebSocket disconnected");
        }
        finally
        {
            lifetime.Cancel();
            await CancelTurnAsync();
            control.Dispose();
            logger.LogInformation("Voice WebSocket session ended");
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

    private async Task SendPhaseAsync(WebSocket socket, string phase, CancellationToken cancellationToken)
    {
        await SendTextAsync(socket, $$"""{"type":"phase","value":"{{phase}}"}""", cancellationToken);
        logger.LogDebug("Sent phase: {Phase}", phase);
    }

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
