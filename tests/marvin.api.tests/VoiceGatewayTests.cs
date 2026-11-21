using System.Net;
using System.Net.WebSockets;
using System.Text;
using Marvin.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
