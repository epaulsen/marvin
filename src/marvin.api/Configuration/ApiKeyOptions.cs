using Microsoft.Extensions.Configuration;

namespace Marvin.Api.Configuration;

internal sealed class ApiKeyOptions
{
    [ConfigurationKeyName("OPENAI_API_KEY")]
    public string? OpenAi { get; set; }

    [ConfigurationKeyName("OPPER_API_KEY")]
    public string? Opper { get; set; }
}
