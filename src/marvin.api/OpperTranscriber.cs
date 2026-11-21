using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Marvin.Api;

internal interface IVoiceTranscriber
{
    Task<string> TranscribeAsync(byte[] audio, CancellationToken cancellationToken);
}

internal sealed class OpperTranscriber(
    HttpClient client,
    IConfiguration configuration,
    ILogger<OpperTranscriber> logger) : IVoiceTranscriber
{
    private const string Model = "berget/NbAiLab/nb-whisper-large";

    public async Task<string> TranscribeAsync(byte[] audio, CancellationToken cancellationToken)
    {
        if (audio.Length % 2 != 0)
            throw new ArgumentException("PCM16 audio must contain complete samples.", nameof(audio));

        var wav = CreateWav(audio);
        using var request = new HttpRequestMessage(HttpMethod.Post, "v3/audio/transcriptions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration["OPPER_API_KEY"]);
        request.Content = JsonContent.Create(new
        {
            model = Model,
            audio = "data:audio/wav;base64," + Convert.ToBase64String(wav),
            language = "no"
        });
        logger.LogDebug("Sending transcription request to Opper; model: {Model}, WAV bytes: {AudioBytes}", Model, wav.Length);
        using var response = await client.SendAsync(request, cancellationToken);
        logger.LogDebug("Opper transcription response status: {StatusCode}", (int)response.StatusCode);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<TranscriptionResponse>(cancellationToken);
        return result?.Text ?? throw new InvalidDataException("Opper returned a transcription without text.");
    }

    private static byte[] CreateWav(byte[] audio)
    {
        var wav = new byte[44 + audio.Length];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 16_000);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), 32_000);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), audio.Length);
        audio.CopyTo(wav, 44);
        return wav;
    }

    private sealed record TranscriptionResponse(string? Text);
}
