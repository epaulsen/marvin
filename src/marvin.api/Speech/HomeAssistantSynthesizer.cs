using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Marvin.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Marvin.Api.Speech;

internal interface IVoiceSynthesizer
{
    Task<byte[]?> SynthesizeAsync(string text, CancellationToken cancellationToken);
}

internal sealed class HomeAssistantSynthesizer(
    HttpClient client,
    IOptions<MarvinOptions> options,
    ILogger<HomeAssistantSynthesizer> logger) : IVoiceSynthesizer
{
    private const int MaxWavBytes = 16 * 1024 * 1024;
    private const string ProxyPrefix = "/api/tts_proxy/";

    internal static bool IsConfigured(MarvinOptions configuration) =>
        !string.IsNullOrWhiteSpace(configuration.HomeAssistantUrl)
        && !string.IsNullOrWhiteSpace(configuration.HomeAssistantToken)
        && !string.IsNullOrWhiteSpace(configuration.HomeAssistantTtsEntity);

    public async Task<byte[]?> SynthesizeAsync(string text, CancellationToken cancellationToken)
    {
        var configuration = options.Value;
        if (!IsConfigured(configuration))
        {
            logger.LogWarning("Skipping speech synthesis: Home Assistant TTS is not configured");
            return null;
        }

        if (!Uri.TryCreate(configuration.HomeAssistantUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(baseUri.UserInfo)
            || baseUri.AbsolutePath != "/" || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment))
            throw new InvalidOperationException("HomeAssistant:Url must be an HTTP(S) origin without a path.");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "/api/tts_get_url"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.HomeAssistantToken);
        request.Content = JsonContent.Create(new
        {
            engine_id = configuration.HomeAssistantTtsEntity,
            message = text,
            cache = false,
            options = new
            {
                preferred_format = "wav",
                preferred_sample_rate = 24_000,
                preferred_sample_channels = 1,
                preferred_sample_bytes = 2
            }
        });

        logger.LogDebug("Requesting speech synthesis from Home Assistant");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<TtsResponse>(cancellationToken);
        var path = result?.Path;
        if (path is null || !path.StartsWith(ProxyPrefix, StringComparison.Ordinal)
            || path.Length == ProxyPrefix.Length
            || path[ProxyPrefix.Length..].Contains("..", StringComparison.Ordinal)
            || path[ProxyPrefix.Length..].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new InvalidDataException("Home Assistant returned an invalid TTS audio path.");

        using var audioRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path));
        using var audioResponse = await client.SendAsync(audioRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        audioResponse.EnsureSuccessStatusCode();
        if (audioResponse.Content.Headers.ContentLength > MaxWavBytes)
            throw new InvalidDataException("Home Assistant TTS audio exceeds the size limit.");

        await using var stream = await audioResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var wav = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (wav.Length + count > MaxWavBytes)
                throw new InvalidDataException("Home Assistant TTS audio exceeds the size limit.");
            wav.Write(buffer, 0, count);
        }

        var pcm = ReadPcm(wav.ToArray());
        logger.LogDebug("Home Assistant speech synthesis completed; PCM bytes: {AudioBytes}", pcm.Length);
        return pcm;
    }

    private static byte[] ReadPcm(byte[] wav)
    {
        if (wav.Length < 12 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Home Assistant TTS did not return WAV audio.");

        var formatValid = false;
        for (var offset = 12; offset <= wav.Length - 8;)
        {
            var chunk = wav.AsSpan(offset, 4);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(offset + 4, 4));
            offset += 8;
            if (chunk.SequenceEqual("data"u8))
            {
                var available = wav.Length - offset;
                var audioLength = length == uint.MaxValue ? available : (long)length;
                if (!formatValid || audioLength == 0 || audioLength > available || audioLength % 2 != 0)
                    throw new InvalidDataException("Home Assistant returned invalid WAV audio data.");
                return wav.AsSpan(offset, (int)audioLength).ToArray();
            }

            if (length > wav.Length - offset)
                throw new InvalidDataException("Home Assistant returned a truncated WAV chunk.");
            if (chunk.SequenceEqual("fmt "u8))
            {
                if (length < 16
                    || BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(offset, 2)) != 1
                    || BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(offset + 2, 2)) != 1
                    || BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(offset + 4, 4)) != 24_000
                    || BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(offset + 12, 2)) != 2
                    || BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(offset + 14, 2)) != 16)
                    throw new InvalidDataException("Home Assistant returned an unsupported WAV format.");
                formatValid = true;
            }
            var next = (long)offset + length + (length & 1);
            if (next > wav.Length)
                throw new InvalidDataException("Home Assistant returned a truncated WAV chunk.");
            offset = (int)next;
        }

        throw new InvalidDataException("Home Assistant returned WAV audio without PCM data.");
    }

    private sealed record TtsResponse(string? Path);
}
