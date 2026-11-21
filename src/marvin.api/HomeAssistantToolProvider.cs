using System.Text.Json;
using ModelContextProtocol.Client;
using OpenAI.Chat;

namespace Marvin.Api;

internal interface IAgentToolProvider
{
    Task<IAgentToolSession?> OpenAsync(CancellationToken cancellationToken);
}

internal interface IAgentToolSession : IAsyncDisposable
{
    IReadOnlyList<ChatTool> Tools { get; }
    Task<string> CallAsync(string name, string arguments, CancellationToken cancellationToken);
}

internal sealed class HomeAssistantToolProvider(IConfiguration configuration, IHttpClientFactory clientFactory) : IAgentToolProvider
{
    public async Task<IAgentToolSession?> OpenAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue<bool>("HomeAssistant:McpEnabled"))
            return null;

        if (!Uri.TryCreate(configuration["HomeAssistant:Url"], UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(baseUri.UserInfo) || baseUri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment)
            || string.IsNullOrWhiteSpace(configuration["HomeAssistant:Token"]))
            throw new InvalidOperationException("Home Assistant MCP requires a valid URL and token.");

        var http = clientFactory.CreateClient("home-assistant-mcp");
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(baseUri, "/api/mcp/assist"),
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + configuration["HomeAssistant:Token"]
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

    private sealed class HomeAssistantToolSession : IAgentToolSession
    {
        private readonly HttpClientTransport transport;
        private readonly McpClient client;
        private readonly Dictionary<string, McpClientTool> tools;

        public HomeAssistantToolSession(HttpClientTransport transport, McpClient client, IEnumerable<McpClientTool> available)
        {
            this.transport = transport;
            this.client = client;
            tools = available.Where(tool => tool.Name.Length is > 0 and <= 64
                && tool.Name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
                && tool.JsonSchema.ValueKind == JsonValueKind.Object)
                .ToDictionary(tool => tool.Name, StringComparer.Ordinal);
            Tools = tools.Values.Select(tool => ChatTool.CreateFunctionTool(tool.Name, tool.Description,
                BinaryData.FromString(tool.JsonSchema.GetRawText()))).ToArray();
        }

        public IReadOnlyList<ChatTool> Tools { get; }

        public async Task<string> CallAsync(string name, string arguments, CancellationToken cancellationToken)
        {
            if (!tools.TryGetValue(name, out var tool) || arguments.Length > 32_768)
                throw new InvalidDataException("OpenAI requested an invalid Home Assistant tool.");

            Dictionary<string, object?>? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("OpenAI returned invalid tool arguments.", ex);
            }
            if (parsed is null)
                throw new InvalidDataException("OpenAI returned invalid tool arguments.");

            var result = await tool.CallAsync(parsed, cancellationToken: cancellationToken);
            var json = JsonSerializer.Serialize(result);
            if (json.Length > 32_768)
                throw new InvalidDataException("Home Assistant returned an oversized tool result.");
            return json;
        }

        public async ValueTask DisposeAsync()
        {
            await client.DisposeAsync();
            await transport.DisposeAsync();
        }
    }
}
