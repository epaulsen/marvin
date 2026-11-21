namespace Marvin.Api;

internal interface IVoiceTurnProcessor
{
    // The response, if present, must be 24 kHz mono PCM16 little-endian.
    Task<byte[]?> ProcessAsync(byte[] audio, CancellationToken cancellationToken);
}

internal sealed class TranscribingVoiceTurnProcessor(
    IVoiceTranscriber transcriber,
    IVoiceSynthesizer synthesizer,
    IConfiguration configuration,
    ILogger<TranscribingVoiceTurnProcessor> logger) : IVoiceTurnProcessor
{
    public async Task<byte[]?> ProcessAsync(byte[] audio, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration["OPPER_API_KEY"]))
        {
            logger.LogWarning("Skipping transcription: OPPER_API_KEY is not configured");
            return null;
        }

        logger.LogDebug("Starting transcription for {AudioBytes} PCM bytes", audio.Length);
        var text = await transcriber.TranscribeAsync(audio, cancellationToken);
        if (configuration.GetValue<bool>("Marvin:LogTranscripts"))
            logger.LogInformation("Transcript: {Transcript}", text);
        else
            logger.LogInformation("Transcription completed");

        if (string.IsNullOrWhiteSpace(text))
            return null;

        return await synthesizer.SynthesizeAsync(text, cancellationToken);
    }
}
