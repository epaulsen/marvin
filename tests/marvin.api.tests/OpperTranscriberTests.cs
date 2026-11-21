using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using Marvin.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Marvin.Api.Tests;

public sealed class OpperTranscriberTests
{
    [Fact]
    public async Task SendsEntirePcmAsWavAndReturnsTranscript()
    {
        var audio = new byte[] { 1, 2, 3, 4, 5, 6 };
        var handler = new TestHandler(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.opper.ai/v3/audio/transcriptions", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization.Parameter);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("berget/NbAiLab/nb-whisper-large", json.RootElement.GetProperty("model").GetString());
            Assert.Equal("no", json.RootElement.GetProperty("language").GetString());
            var source = json.RootElement.GetProperty("audio").GetString()!;
            Assert.StartsWith("data:audio/wav;base64,", source);
            var wav = Convert.FromBase64String(source["data:audio/wav;base64,".Length..]);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
            Assert.Equal(wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
            Assert.Equal("WAVEfmt ", Encoding.ASCII.GetString(wav, 8, 8));
            Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)));
            Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));
            Assert.Equal(16_000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
            Assert.Equal(32_000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(28)));
            Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(32)));
            Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
            Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
            Assert.Equal(audio.Length, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
            Assert.Equal(audio, wav[44..]);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"text":"Hei Marvin"}""", Encoding.UTF8, "application/json")
            };
        });

        var result = await CreateTranscriber(handler).TranscribeAsync(audio, TestContext.Current.CancellationToken);
        Assert.Equal("Hei Marvin", result);
    }

    [Fact]
    public async Task FailedResponseIsReported()
    {
        var handler = new TestHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateTranscriber(handler).TranscribeAsync([0, 0], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingTranscriptIsRejected()
    {
        var handler = new TestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        }));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateTranscriber(handler).TranscribeAsync([0, 0], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationInterruptsRequest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TestHandler(async (_, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cancellation = new CancellationTokenSource();
        var task = CreateTranscriber(handler).TranscribeAsync([0, 0], cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    private static OpperTranscriber CreateTranscriber(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["OPPER_API_KEY"] = "test-key" }).Build();
        return new OpperTranscriber(new HttpClient(handler) { BaseAddress = new Uri("https://api.opper.ai/") },
            configuration, NullLogger<OpperTranscriber>.Instance);
    }

    private sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
