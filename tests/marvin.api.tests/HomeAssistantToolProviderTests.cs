using System.Net;
using System.Text;
using System.Text.Json;
using Marvin.Api;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Marvin.Api.Tests;

public sealed class HomeAssistantToolProviderTests
{
    [Fact]
    public async Task DiscoversAndCallsHomeAssistantMcpTools()
    {
        var handler = new McpHandler();
        var provider = new HomeAssistantToolProvider(Configuration(true), new TestHttpClientFactory(handler));
        await using var session = await provider.OpenAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        Assert.Single(session.Tools);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            session.CallAsync("ask_user", """{"question":"Test?"}""", TestContext.Current.CancellationToken));
        var result = await session.CallAsync("light__HassTurnOn", """{"name":"Kjøkken"}""",
            TestContext.Current.CancellationToken);
        Assert.Contains("Done", result.Content);
        Assert.False(result.IsError);
        Assert.Equal("Kjøkken", handler.LightName);
        Assert.Contains("initialize", handler.Methods);
        Assert.Contains("tools/list", handler.Methods);
        Assert.Contains("tools/call", handler.Methods);
    }

    [Fact]
    public async Task PreservesMcpToolErrorStatus()
    {
        var handler = new McpHandler(toolIsError: true);
        var provider = new HomeAssistantToolProvider(Configuration(true), new TestHttpClientFactory(handler));
        await using var session = await provider.OpenAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        var result = await session.CallAsync("light__HassTurnOn", """{"name":"Kjøkken"}""",
            TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Contains("Done", result.Content);
    }

    [Fact]
    public async Task DisabledMcpDoesNotConnect()
    {
        var handler = new McpHandler();
        var provider = new HomeAssistantToolProvider(Configuration(false), new TestHttpClientFactory(handler));
        Assert.Null(await provider.OpenAsync(TestContext.Current.CancellationToken));
        Assert.Empty(handler.Methods);
    }

    private static IConfiguration Configuration(bool enabled) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HomeAssistant:Url"] = "http://homeassistant.local:8123",
            ["HomeAssistant:Token"] = "test-token",
            ["HomeAssistant:McpEnabled"] = enabled.ToString()
        }).Build();

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class McpHandler(bool toolIsError = false) : HttpMessageHandler
    {
        public List<string> Methods { get; } = [];
        public string? LightName { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("http://homeassistant.local:8123/api/mcp/assist", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-token", request.Headers.Authorization.Parameter);
            if (request.Method == HttpMethod.Delete)
                return new HttpResponseMessage(HttpStatusCode.NoContent);

            Assert.Equal(HttpMethod.Post, request.Method);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = json.RootElement;
            var method = root.GetProperty("method").GetString()!;
            Methods.Add(method);
            if (!root.TryGetProperty("id", out var id))
                return new HttpResponseMessage(HttpStatusCode.Accepted);

            if (method == "server/discover")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$$"""{"jsonrpc":"2.0","id":{{{id.GetRawText()}}},"error":{"code":-32601,"message":"Method not found"}}""",
                        Encoding.UTF8, "application/json")
                };

            string result;
            switch (method)
            {
                case "initialize":
                    result = """{"protocolVersion":"2025-03-26","capabilities":{"tools":{}},"serverInfo":{"name":"fake-ha","version":"1"}}""";
                    break;
                case "tools/list":
                    result = """{"tools":[{"name":"light__HassTurnOn","description":"Turn on light","inputSchema":{"type":"object","properties":{"name":{"type":"string"}}}},{"name":"ask_user","description":"Conflicting tool","inputSchema":{"type":"object","properties":{}}}]}""";
                    break;
                case "tools/call":
                    Assert.Equal("light__HassTurnOn", root.GetProperty("params").GetProperty("name").GetString());
                    LightName = root.GetProperty("params").GetProperty("arguments").GetProperty("name").GetString();
                    result = $$"""{"content":[{"type":"text","text":"Done"}],"isError":{{(toolIsError ? "true" : "false")}}}""";
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected MCP method: {method}");
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"jsonrpc":"2.0","id":{{id.GetRawText()}},"result":{{result}}}""",
                    Encoding.UTF8, "application/json")
            };
        }
    }
}
