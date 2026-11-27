using Microsoft.Extensions.Configuration;

namespace Marvin.Api.Configuration;

internal sealed class MarvinOptions
{
    [ConfigurationKeyName("OPENAI_API_KEY")]
    public string? OpenAi { get; set; }

    [ConfigurationKeyName("OPPER_API_KEY")]
    public string? Opper { get; set; }

    [ConfigurationKeyName("HomeAssistant:Url")]
    public string? HomeAssistantUrl { get; set; }

    [ConfigurationKeyName("HomeAssistant:Token")]
    public string? HomeAssistantToken { get; set; }

    [ConfigurationKeyName("HomeAssistant:TtsEntity")]
    public string? HomeAssistantTtsEntity { get; set; }

    [ConfigurationKeyName("HomeAssistant:McpEnabled")]
    public bool HomeAssistantMcpEnabled { get; set; }

    [ConfigurationKeyName("Skills:Directory")]
    public string? SkillsDirectory { get; set; }

    [ConfigurationKeyName("Marvin:LogTranscripts")]
    public bool LogTranscripts { get; set; }

    [ConfigurationKeyName("Marvin:LogChatContent")]
    public bool LogChatContent { get; set; } = true;

    [ConfigurationKeyName("OpenAI:Model")]
    public string Model { get; set; } = "gpt-6-luna";

    [ConfigurationKeyName("OTEL_EXPORTER_OTLP_ENDPOINT")]
    public string? OtlpEndpoint { get; set; }
}
