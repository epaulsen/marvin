using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using Marvin.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Marvin.Api.Tests;

public sealed class HomeAssistantSynthesizerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestsWavAndReturnsPcm(bool streamingWav)
    {
        var pcm = new byte[] { 1, 2, 3, 4 };
        var calls = 0;
        var handler = new TestHandler(async (request, cancellationToken) =>
        {
            calls++;
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("http://homeassistant.local:8123/api/tts_get_url", request.RequestUri!.ToString());
                Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
                Assert.Equal("test-token", request.Headers.Authorization.Parameter);
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.Equal("tts.test", json.RootElement.GetProperty("engine_id").GetString());
                Assert.Equal("Hei Marvin", json.RootElement.GetProperty("message").GetString());
                Assert.False(json.RootElement.GetProperty("cache").GetBoolean());
                var options = json.RootElement.GetProperty("options");
                Assert.Equal("wav", options.GetProperty("preferred_format").GetString());
                Assert.Equal(24_000, options.GetProperty("preferred_sample_rate").GetInt32());
                Assert.Equal(1, options.GetProperty("preferred_sample_channels").GetInt32());
                Assert.Equal(2, options.GetProperty("preferred_sample_bytes").GetInt32());
                return Json("""{"path":"/api/tts_proxy/test-token.wav","url":"http://different-host/api/tts_proxy/test-token.wav"}""");
            }

            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("http://homeassistant.local:8123/api/tts_proxy/test-token.wav", request.RequestUri!.ToString());
            Assert.Null(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Wav(pcm, streamingWav)) };
        });

        Assert.Equal(pcm, await CreateSynthesizer(handler).SynthesizeAsync("Hei Marvin", TestContext.Current.CancellationToken));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("https://evil.example/api/tts_proxy/file.wav")]
    [InlineData("//evil.example/api/tts_proxy/file.wav")]
    [InlineData("/api/tts_proxy/../other")]
    [InlineData("/api/tts_proxy/file.wav?token=other")]
    public async Task RejectsUnsafeAudioPath(string path)
    {
        var calls = 0;
        var handler = new TestHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Json(JsonSerializer.Serialize(new { path })));
        });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateSynthesizer(handler).SynthesizeAsync("Hei", TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RejectsUnexpectedAudioFormat()
    {
        var wav = Wav([1, 2]);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), 16_000);
        var handler = AudioHandler(wav);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateSynthesizer(handler).SynthesizeAsync("Hei", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RejectsTruncatedAudio()
    {
        var wav = Wav([1, 2, 3, 4]);
        var handler = AudioHandler(wav[..^1]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateSynthesizer(handler).SynthesizeAsync("Hei", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReportsHomeAssistantErrors()
    {
        var handler = new TestHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateSynthesizer(handler).SynthesizeAsync("Hei", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingConfigurationSkipsTts()
    {
        var handler = new TestHandler((_, _) => throw new InvalidOperationException("Request was not expected"));
        var synthesizer = new HomeAssistantSynthesizer(new HttpClient(handler), new ConfigurationBuilder().Build(),
            NullLogger<HomeAssistantSynthesizer>.Instance);
        Assert.Null(await synthesizer.SynthesizeAsync("Hei", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationInterruptsAudioDownload()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TestHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Post)
                return Json("""{"path":"/api/tts_proxy/test.wav"}""");
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cancellation = new CancellationTokenSource();
        var task = CreateSynthesizer(handler).SynthesizeAsync("Hei", cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    private static TestHandler AudioHandler(byte[] wav) => new((request, _) =>
        Task.FromResult(request.Method == HttpMethod.Post
            ? Json("""{"path":"/api/tts_proxy/test.wav"}""")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(wav) }));

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private static byte[] Wav(byte[] pcm, bool streaming = false)
    {
        var wav = new byte[44 + pcm.Length];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), streaming ? uint.MaxValue : (uint)(wav.Length - 8));
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), 24_000);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), 48_000);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), streaming ? uint.MaxValue : (uint)pcm.Length);
        pcm.CopyTo(wav, 44);
        return wav;
    }

    private static HomeAssistantSynthesizer CreateSynthesizer(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HomeAssistant:Url"] = "http://homeassistant.local:8123",
            ["HomeAssistant:Token"] = "test-token",
            ["HomeAssistant:TtsEntity"] = "tts.test"
        }).Build();
        return new HomeAssistantSynthesizer(new HttpClient(handler), configuration,
            NullLogger<HomeAssistantSynthesizer>.Instance);
    }

    private sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
