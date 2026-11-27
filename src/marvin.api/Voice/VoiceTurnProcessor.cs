using Marvin.Api.Configuration;
using Marvin.Api.Chat;
using Marvin.Api.Speech;
using Marvin.Api.Telemetry;
using Microsoft.Extensions.Options;

namespace Marvin.Api.Voice;

internal interface IVoiceTurnProcessor
{
    // The response, if present, must be 24 kHz mono PCM16 little-endian.
    Task<VoiceTurnResponse?> ProcessAsync(byte[] audio, CancellationToken cancellationToken);
}

internal sealed record VoiceTurnResponse(byte[] Audio, bool RequestFollowUp = false);

internal sealed class TranscribingVoiceTurnProcessor(
    IVoiceTranscriber transcriber,
    IVoiceSynthesizer synthesizer,
    IVoiceAssistant assistant,
    IOptions<ApiKeyOptions> apiKeys,
    IOptions<MarvinOptions> options,
    ChatTelemetry telemetry,
    ILogger<TranscribingVoiceTurnProcessor> logger) : IVoiceTurnProcessor
{
    public async Task<VoiceTurnResponse?> ProcessAsync(byte[] audio, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKeys.Value.Opper))
        {
            logger.LogWarning("Skipping transcription: OPPER_API_KEY is not configured");
            return null;
        }

        logger.LogDebug("Starting transcription for {AudioBytes} PCM bytes", audio.Length);
        var text = await transcriber.TranscribeAsync(audio, cancellationToken);
        if (options.Value.LogTranscripts)
            logger.LogInformation("Transcript: {Transcript}", telemetry.Sanitize(text));
        else
            logger.LogInformation("Transcription completed");

        if (string.IsNullOrWhiteSpace(text))
            return null;

        var reply = await assistant.ReplyAsync(text, cancellationToken);
        var speech = await synthesizer.SynthesizeAsync(reply.Text, cancellationToken);
        return speech is { Length: > 0 } ? new VoiceTurnResponse(speech, reply.RequestFollowUp) : null;
    }
}
