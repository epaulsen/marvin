namespace Marvin.Api.Configuration;

internal sealed class HomeAssistantOptions
{
    public string? Url { get; set; }
    public string? Token { get; set; }
    public string? TtsEntity { get; set; }
    public bool McpEnabled { get; set; }
}
