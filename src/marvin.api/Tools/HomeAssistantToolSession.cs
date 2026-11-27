using System.Text.Json;
using ModelContextProtocol.Client;
using OpenAI.Chat;

namespace Marvin.Api.Tools;

internal sealed class HomeAssistantToolSession : IAgentToolSession
{
    private readonly HttpClientTransport transport;
    private readonly McpClient client;
    private readonly Dictionary<string, McpClientTool> tools;

    public HomeAssistantToolSession(HttpClientTransport transport, McpClient client, IEnumerable<McpClientTool> available)
    {
        this.transport = transport;
        this.client = client;
        tools = available.Where(tool => tool.Name != AskUserTool.Name
            && tool.Name != LoadSkillTool.Name
            && tool.Name.Length is > 0 and <= 64
            && tool.Name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            && tool.JsonSchema.ValueKind == JsonValueKind.Object)
            .ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        Tools = tools.Values.Select(tool => ChatTool.CreateFunctionTool(tool.Name, tool.Description,
            BinaryData.FromString(tool.JsonSchema.GetRawText()))).ToArray();
    }

    public IReadOnlyList<ChatTool> Tools { get; }

    public async Task<AgentToolResult> CallAsync(string name, string arguments, CancellationToken cancellationToken)
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
        return new AgentToolResult(json, result.IsError == true);
    }

    public async ValueTask DisposeAsync()
    {
        await client.DisposeAsync();
        await transport.DisposeAsync();
    }
}
