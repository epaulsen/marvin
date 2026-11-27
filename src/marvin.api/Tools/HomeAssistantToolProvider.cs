using ModelContextProtocol.Client;
using OpenAI.Chat;
using Marvin.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Marvin.Api.Tools;

internal interface IAgentToolProvider
{
    Task<IAgentToolSession?> OpenAsync(CancellationToken cancellationToken);
}

internal interface IAgentToolSession : IAsyncDisposable
{
    IReadOnlyList<ChatTool> Tools { get; }
    Task<AgentToolResult> CallAsync(string name, string arguments, CancellationToken cancellationToken);
}

internal sealed record AgentToolResult(string Content, bool IsError = false);

internal sealed class HomeAssistantToolProvider(IOptions<HomeAssistantOptions> options, IHttpClientFactory clientFactory) : IAgentToolProvider
{
    public async Task<IAgentToolSession?> OpenAsync(CancellationToken cancellationToken)
    {
        var configuration = options.Value;
        if (!configuration.McpEnabled)
            return null;

        if (!Uri.TryCreate(configuration.Url, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(baseUri.UserInfo) || baseUri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment)
            || string.IsNullOrWhiteSpace(configuration.Token))
            throw new InvalidOperationException("Home Assistant MCP requires a valid URL and token.");

        var http = clientFactory.CreateClient("home-assistant-mcp");
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(baseUri, "/api/mcp/assist"),
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + configuration.Token
            }
        }, http, ownsHttpClient: true);
        McpClient? client = null;
        try
        {
            client = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                InitializationTimeout = TimeSpan.FromSeconds(10),
                DiscoverProbeTimeout = TimeSpan.FromSeconds(1)
            }, cancellationToken: cancellationToken);
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            return new HomeAssistantToolSession(transport, client, tools);
        }
        catch
        {
            if (client is not null)
                await client.DisposeAsync();
            await transport.DisposeAsync();
            throw;
        }
    }
}
