using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Marvin.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Marvin.Api.Tests;

public sealed class VoiceGatewayTests
{
    [Theory(Timeout = 10_000)]
    [InlineData("/health", HttpStatusCode.OK)]
    [InlineData("/voice", HttpStatusCode.BadRequest)]
    public async Task HttpEndpointsReturnExpectedStatus(string path, HttpStatusCode status)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(status, response.StatusCode);
    }

    [Fact]
    public void DevelopmentSettingsAreLoadedAndEnvironmentOverridesThem()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"marvin-config-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var baseFile = Path.Combine(directory, "appsettings.json");
        var developmentFile = Path.Combine(directory, "appsettings.Development.json");
        const string variable = "MarvinConfigTest__EnvOverride";
        var previousValue = Environment.GetEnvironmentVariable(variable);
        try
        {
            File.WriteAllText(baseFile, """{"MarvinConfigTest":{"BaseOnly":"base","Override":"base","EnvOverride":"base"}}""");
            File.WriteAllText(developmentFile, """{"MarvinConfigTest":{"Override":"development","EnvOverride":"development"}}""");
            Environment.SetEnvironmentVariable(variable, "environment");
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(directory);
                builder.UseEnvironment("Development");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IVoiceActivityDetectorFactory>();
                    services.AddSingleton<IVoiceActivityDetectorFactory, TestDetectorFactory>();
                });
            });
            var configuration = factory.Services.GetRequiredService<IConfiguration>();
            Assert.Equal("base", configuration["MarvinConfigTest:BaseOnly"]);
            Assert.Equal("development", configuration["MarvinConfigTest:Override"]);
            Assert.Equal("environment", configuration["MarvinConfigTest:EnvOverride"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previousValue);
            File.Delete(baseFile);
            File.Delete(developmentFile);
            Directory.Delete(directory);
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task WakeSpeechAndSilenceProducePhasesAndReply()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var processor = new TestProcessor([1, 2, 3, 4]);
        await using var factory = CreateFactory(processor);
        using var socket = await ConnectAsync(factory);
        await AssertTextAsync(socket, """{"type":"hello","audio_out":"pcm","follow_up_ms":0}""");
        await SendTextAsync(socket, """{"type":"start"}""");
        await SendTextAsync(socket, """{"type":"wake"}""");
        await SendAudioAsync(socket, Speech());
        await AssertTextAsync(socket, """{"type":"phase","value":"listening"}""");
        await SendAudioAsync(socket, new byte[25 * 1024]);
        await AssertTextAsync(socket, """{"type":"phase","value":"thinking"}""");
        await AssertTextAsync(socket, """{"type":"phase","value":"replying"}""");
        var (type, response) = await ReceiveAsync(socket);
        Assert.Equal(WebSocketMessageType.Binary, type);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, response);
        await AssertTextAsync(socket, """{"type":"phase","value":"idle"}""");
        Assert.Equal(26 * 1024, processor.LastAudio!.Length);
    }

    [Fact(Timeout = 10_000)]
    public async Task CompletedUtteranceIsTranscribedAndSpoken()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var transcriber = new TestTranscriber();
        var synthesizer = new TestSynthesizer([1, 2, 3, 4]);
        var processor = new TranscribingVoiceTurnProcessor(
            transcriber, synthesizer, new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["OPPER_API_KEY"] = "test-key" }).Build(),
            NullLogger<TranscribingVoiceTurnProcessor>.Instance);
        await using var factory = CreateFactory(processor);
        using var socket = await ConnectAsync(factory);
        await AssertTextAsync(socket, """{"type":"hello","audio_out":"pcm","follow_up_ms":0}""");
        await SendTextAsync(socket, """{"type":"start"}""");
        await SendTextAsync(socket, """{"type":"wake"}""");
        await SendAudioAsync(socket, Speech());
        await AssertTextAsync(socket, """{"type":"phase","value":"listening"}""");
        await SendAudioAsync(socket, new byte[25 * 1024]);
        await AssertTextAsync(socket, """{"type":"phase","value":"thinking"}""");
        await AssertTextAsync(socket, """{"type":"phase","value":"replying"}""");
        var (type, response) = await ReceiveAsync(socket);
        Assert.Equal(WebSocketMessageType.Binary, type);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, response);
        await AssertTextAsync(socket, """{"type":"phase","value":"idle"}""");
        var expected = new byte[26 * 1024];
        expected[0] = 1;
        Assert.Equal(expected, transcriber.LastAudio);
        Assert.Equal("Hei Marvin", synthesizer.Text);
    }

    [Fact(Timeout = 10_000)]
    public async Task ConfiguredGatewaySendsCompletedUtteranceToOpper()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var handler = new TranscriptionHandler();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["OPPER_API_KEY"] = "test-key" }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IVoiceActivityDetectorFactory>();
                services.AddSingleton<IVoiceActivityDetectorFactory, TestDetectorFactory>();
                services.AddHttpClient<IVoiceTranscriber, OpperTranscriber>()
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
            });
        });
        using var socket = await ConnectAsync(factory);
        await AssertTextAsync(socket, """{"type":"hello","audio_out":"pcm","follow_up_ms":0}""");
        await SendTextAsync(socket, """{"type":"start"}""");
        await SendTextAsync(socket, """{"type":"wake"}""");
        await SendAudioAsync(socket, Speech());
        await AssertTextAsync(socket, """{"type":"phase","value":"listening"}""");
        await SendAudioAsync(socket, new byte[25 * 1024]);
        await AssertTextAsync(socket, """{"type":"phase","value":"thinking"}""");
        await AssertTextAsync(socket, """{"type":"phase","value":"idle"}""");
        var expected = new byte[26 * 1024];
        expected[0] = 1;
        Assert.Equal(expected, handler.Audio);
    }

    [Fact(Timeout = 10_000)]
    public async Task ConfiguredGatewayReturnsHomeAssistantSpeech()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var tts = new SpeechHandler();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["OPPER_API_KEY"] = "test-key",
                    ["HomeAssistant:Url"] = "http://homeassistant.local:8123",
                    ["HomeAssistant:Token"] = "test-token",
                    ["HomeAssistant:TtsEntity"] = "tts.test"
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IVoiceActivityDetectorFactory>();
                services.AddSingleton<IVoiceActivityDetectorFactory, TestDetectorFactory>();
                services.AddHttpClient<IVoiceTranscriber, OpperTranscriber>()
                    .ConfigurePrimaryHttpMessageHandler(() => new TranscriptionHandler());
                services.AddHttpClient<IVoiceSynthesizer, HomeAssistantSynthesizer>()
                    .ConfigurePrimaryHttpMessageHandler(() => tts);
            });
        });
        using var socket = await ConnectAsync(factory);
        await AssertTextAsync(socket, """{"type":"hello","audio_out":"pcm","follow_up_ms":0}""");
        await SendTextAsync(socket, """{"type":"start"}""");
        await SendTextAsync(socket, """{"type":"wake"}""");
        await SendAudioAsync(socket, Speech());
        await AssertTextAsync(socket, """{"type":"phase","value":"listening"}""");
        await SendAudioAsync(socket, new byte[25 * 1024]);
        await AssertTextAsync(socket, """{"type":"phase","value":"thinking"}""");
        await AssertTextAsync(socket, """{"type":"phase","value":"replying"}""");
        var (type, response) = await ReceiveAsync(socket);
        Assert.Equal(WebSocketMessageType.Binary, type);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, response);
        await AssertTextAsync(socket, """{"type":"phase","value":"idle"}""");
        Assert.Equal("Hei Marvin", tts.Text);
    }

    [Fact]
    public async Task WithoutApiKeyAudioIsNotTranscribed()
    {
        var transcriber = new TestTranscriber();
        var processor = new TranscribingVoiceTurnProcessor(
            transcriber, new TestSynthesizer(null), new ConfigurationBuilder().Build(),
            NullLogger<TranscribingVoiceTurnProcessor>.Instance);
        Assert.Null(await processor.ProcessAsync([0, 0], TestContext.Current.CancellationToken));
        Assert.Null(transcriber.LastAudio);
    }

    [Fact]
    public async Task EmptyTranscriptDoesNotTriggerSpeech()
    {
        var synthesizer = new TestSynthesizer([1, 2]);
        var processor = new TranscribingVoiceTurnProcessor(
            new TestTranscriber("  "), synthesizer,
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["OPPER_API_KEY"] = "test-key" }).Build(),
            NullLogger<TranscribingVoiceTurnProcessor>.Instance);
        Assert.Null(await processor.ProcessAsync([0, 0], TestContext.Current.CancellationToken));
        Assert.Null(synthesizer.Text);
    }

    [Fact(Timeout = 10_000)]
    public async Task IndependentConnectionsCanSpeakConcurrentlyAndReuseConnection()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        await using var factory = CreateFactory();
        using var first = await ConnectAsync(factory);
        using var second = await ConnectAsync(factory);
        await AssertTextAsync(first, """{"type":"hello","audio_out":"pcm","follow_up_ms":0}""");
        await AssertTextAsync(second, """{"type":"hello","audio_out":"pcm","follow_up_ms":0}""");
        foreach (var socket in new[] { first, second })
        {
            await SendTextAsync(socket, """{"type":"start"}""");
            await SendTextAsync(socket, """{"type":"wake"}""");
        }
        await SendAudioAsync(first, Speech());
        await SendAudioAsync(second, Speech());
        await AssertTextAsync(first, """{"type":"phase","value":"listening"}""");
        await AssertTextAsync(second, """{"type":"phase","value":"listening"}""");
        await SendAudioAsync(first, new byte[25 * 1024]);
        await AssertTextAsync(first, """{"type":"phase","value":"thinking"}""");
        await AssertTextAsync(first, """{"type":"phase","value":"idle"}""");
        await SendAudioAsync(second, new byte[25 * 1024]);
        await AssertTextAsync(second, """{"type":"phase","value":"thinking"}""");
        await AssertTextAsync(second, """{"type":"phase","value":"idle"}""");
        await SendTextAsync(first, """{"type":"wake"}""");
        await SendAudioAsync(first, Speech());
        await AssertTextAsync(first, """{"type":"phase","value":"listening"}""");
    }

    [Fact(Timeout = 10_000)]
    public async Task InterruptCancelsInFlightTurnAndReturnsIdle()
    {
        var processor = new WaitingProcessor();
        await using var factory = CreateFactory(processor);
        using var socket = await ConnectAsync(factory);
        await AssertTextAsync(socket, """{"type":"hello","audio_out":"pcm","follow_up_ms":0}""");
        await SendTextAsync(socket, """{"type":"start"}""");
        await SendTextAsync(socket, """{"type":"wake"}""");
        await SendAudioAsync(socket, Speech());
        await AssertTextAsync(socket, """{"type":"phase","value":"listening"}""");
        await SendAudioAsync(socket, new byte[25 * 1024]);
        await AssertTextAsync(socket, """{"type":"phase","value":"thinking"}""");
        await SendTextAsync(socket, """{"type":"interrupt"}""");
        await AssertTextAsync(socket, """{"type":"phase","value":"idle"}""");
        await processor.Cancelled.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 10_000)]
    public async Task InterruptStopsAudioPlayback()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        await using var factory = CreateFactory(new TestProcessor(new byte[480_000]));
        using var socket = await ConnectAsync(factory);
        await AssertTextAsync(socket, """{"type":"hello","audio_out":"pcm","follow_up_ms":0}""");
        await SendTextAsync(socket, """{"type":"start"}""");
        await SendTextAsync(socket, """{"type":"wake"}""");
        await SendAudioAsync(socket, Speech());
        await AssertTextAsync(socket, """{"type":"phase","value":"listening"}""");
        await SendAudioAsync(socket, new byte[25 * 1024]);
        await AssertTextAsync(socket, """{"type":"phase","value":"thinking"}""");
        await AssertTextAsync(socket, """{"type":"phase","value":"replying"}""");
        var (type, data) = await ReceiveAsync(socket);
        Assert.Equal(WebSocketMessageType.Binary, type);
        Assert.Equal(960, data.Length);
        await SendTextAsync(socket, """{"type":"interrupt"}""");
        await AssertTextAsync(socket, """{"type":"phase","value":"idle"}""");
    }

    [Fact(Timeout = 10_000)]
    public async Task AudioBeforeWakeIsRejected()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        await using var factory = CreateFactory();
        using var socket = await ConnectAsync(factory);
        await AssertTextAsync(socket, """{"type":"hello","audio_out":"pcm","follow_up_ms":0}""");
        await SendTextAsync(socket, """{"type":"start"}""");
        await SendAudioAsync(socket, Speech());
        var (type, _) = await ReceiveAsync(socket);
        Assert.Equal(WebSocketMessageType.Close, type);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
    }

    [Fact(Timeout = 10_000)]
    public async Task FlushDiscardsUnfinishedSpeech()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        await using var factory = CreateFactory();
        using var socket = await ConnectAsync(factory);
        await AssertTextAsync(socket, """{"type":"hello","audio_out":"pcm","follow_up_ms":0}""");
        await SendTextAsync(socket, """{"type":"start"}""");
        await SendTextAsync(socket, """{"type":"wake"}""");
        await SendAudioAsync(socket, Speech());
        await AssertTextAsync(socket, """{"type":"phase","value":"listening"}""");
        await SendTextAsync(socket, """{"type":"flush"}""");
        await SendAudioAsync(socket, new byte[25 * 1024]);
        await SendAudioAsync(socket, Speech());
        await AssertTextAsync(socket, """{"type":"phase","value":"listening"}""");
    }

    [Fact(Timeout = 10_000)]
    public async Task OversizedAudioIsRejected()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        await using var factory = CreateFactory();
        using var socket = await ConnectAsync(factory);
        await AssertTextAsync(socket, """{"type":"hello","audio_out":"pcm","follow_up_ms":0}""");
        await SendTextAsync(socket, """{"type":"start"}""");
        await SendTextAsync(socket, """{"type":"wake"}""");
        await SendAudioAsync(socket, new byte[1_921_024]);
        var (type, _) = await ReceiveAsync(socket);
        Assert.Equal(WebSocketMessageType.Close, type);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, socket.CloseStatus);
    }

    [Fact]
    public void ActualSileroModelAcceptsSilence()
    {
        using var factory = new WebApplicationFactory<Program>();
        var detector = factory.Services.GetRequiredService<IVoiceActivityDetectorFactory>().Create();
        Assert.InRange(detector.Evaluate(new byte[1024]), 0f, 0.5f);
    }

    private static WebApplicationFactory<Program> CreateFactory(IVoiceTurnProcessor? processor = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IVoiceActivityDetectorFactory>();
            services.AddSingleton<IVoiceActivityDetectorFactory, TestDetectorFactory>();
            services.RemoveAll<IVoiceTurnProcessor>();
            services.AddSingleton(processor ?? new TestProcessor(null));
        }));

    private static Task<WebSocket> ConnectAsync(WebApplicationFactory<Program> factory) =>
        factory.Server.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/voice"), TestContext.Current.CancellationToken);

    private static byte[] Speech()
    {
        var data = new byte[1024];
        data[0] = 1;
        return data;
    }

    private static ValueTask SendTextAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text).AsMemory(), WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);

    private static ValueTask SendAudioAsync(WebSocket socket, byte[] audio) =>
        socket.SendAsync(audio.AsMemory(), WebSocketMessageType.Binary, true, TestContext.Current.CancellationToken);

    private static async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveAsync(WebSocket socket)
    {
        var bytes = new byte[1024];
        var result = await socket.ReceiveAsync(bytes.AsMemory(), TestContext.Current.CancellationToken);
        return (result.MessageType, bytes[..result.Count]);
    }

    private static async Task AssertTextAsync(WebSocket socket, string expected)
    {
        var (type, data) = await ReceiveAsync(socket);
        Assert.Equal(WebSocketMessageType.Text, type);
        Assert.Equal(expected, Encoding.UTF8.GetString(data));
    }

    private sealed class TestDetectorFactory : IVoiceActivityDetectorFactory
    {
        public IVoiceActivityDetector Create() => new TestDetector();
    }

    private sealed class TestDetector : IVoiceActivityDetector
    {
        public float Evaluate(ReadOnlySpan<byte> pcm) => pcm[0] == 1 ? 0.9f : 0f;
        public void Reset() { }
    }

    private sealed class TestProcessor(byte[]? response) : IVoiceTurnProcessor
    {
        public byte[]? LastAudio { get; private set; }
        public Task<byte[]?> ProcessAsync(byte[] audio, CancellationToken cancellationToken)
        {
            LastAudio = audio;
            return Task.FromResult(response);
        }
    }

    private sealed class TestTranscriber(string text = "Hei Marvin") : IVoiceTranscriber
    {
        public byte[]? LastAudio { get; private set; }

        public Task<string> TranscribeAsync(byte[] audio, CancellationToken cancellationToken)
        {
            LastAudio = audio;
            return Task.FromResult(text);
        }
    }

    private sealed class TestSynthesizer(byte[]? audio) : IVoiceSynthesizer
    {
        public string? Text { get; private set; }

        public Task<byte[]?> SynthesizeAsync(string text, CancellationToken cancellationToken)
        {
            Text = text;
            return Task.FromResult(audio);
        }
    }

    private sealed class TranscriptionHandler : HttpMessageHandler
    {
        public byte[]? Audio { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var dataUri = json.RootElement.GetProperty("audio").GetString()!;
            var wav = Convert.FromBase64String(dataUri["data:audio/wav;base64,".Length..]);
            Audio = wav[44..];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"text":"Hei Marvin"}""", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class SpeechHandler : HttpMessageHandler
    {
        public string? Text { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Text = json.RootElement.GetProperty("message").GetString();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"path":"/api/tts_proxy/test.wav"}""", Encoding.UTF8, "application/json")
                };
            }

            var wav = Convert.FromHexString(
                "524946462800000057415645666D74201000000001000100C05D000080BB000002001000646174610400000001020304");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(wav) };
        }
    }

    private sealed class WaitingProcessor : IVoiceTurnProcessor
    {
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<byte[]?> ProcessAsync(byte[] audio, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null;
            }
            finally
            {
                Cancelled.TrySetResult();
            }
        }
    }
}
