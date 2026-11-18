using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Marvin.Api.Tests;

public sealed class VoiceGatewayTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory(Timeout = 10_000)]
    [InlineData("/health", HttpStatusCode.OK)]
    [InlineData("/voice", HttpStatusCode.BadRequest)]
    public async Task HttpEndpointsReturnExpectedStatus(string path, HttpStatusCode status)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(status, response.StatusCode);
    }

    [Fact(Timeout = 10_000)]
    public async Task VoiceSocketAcceptsMultipleUtterancesOnOneConnection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var socket = await ConnectAsync(cancellationToken);

        foreach (var bytes in new[] { 8192, 320 })
        {
            await SendStartAsync(socket, cancellationToken);
            await socket.SendAsync(new byte[bytes].AsMemory(), WebSocketMessageType.Binary, true, cancellationToken);
            await socket.SendAsync("""{"type":"end"}"""u8.ToArray().AsMemory(),
                WebSocketMessageType.Text, true, cancellationToken);
        }

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);

        Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.CloseStatus);
    }

    [Theory(Timeout = 10_000)]
    [InlineData(false, 2, WebSocketCloseStatus.PolicyViolation)]
    [InlineData(true, 2_000_000, WebSocketCloseStatus.MessageTooBig)]
    public async Task VoiceSocketRejectsAudioBeforeStartOrAboveLimit(
        bool sendStart, int bytes, WebSocketCloseStatus expectedStatus)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var socket = await ConnectAsync(cancellationToken);

        if (sendStart)
        {
            await SendStartAsync(socket, cancellationToken);
        }

        await socket.SendAsync(new byte[bytes].AsMemory(), WebSocketMessageType.Binary, true, cancellationToken);
        var result = await socket.ReceiveAsync(new byte[1].AsMemory(), cancellationToken);

        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(expectedStatus, socket.CloseStatus);
    }

    private Task<WebSocket> ConnectAsync(CancellationToken cancellationToken) =>
        factory.Server.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/voice"), cancellationToken);

    private static ValueTask SendStartAsync(WebSocket socket, CancellationToken cancellationToken) =>
        socket.SendAsync("""{"type":"start"}"""u8.ToArray().AsMemory(),
            WebSocketMessageType.Text, true, cancellationToken);
}
