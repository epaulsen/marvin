using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Marvin.Api;

internal static partial class ChatTelemetry
{
    internal const string SourceName = "Marvin.Chat";
    internal static readonly ActivitySource Source = new(SourceName);
    private const string Redacted = "[REDACTED]";

    internal static string Sanitize(string value, IConfiguration configuration)
    {
        foreach (var key in new[] { "OPENAI_API_KEY", "OPPER_API_KEY", "HomeAssistant:Token" })
        {
            var secret = configuration[key];
            if (!string.IsNullOrEmpty(secret))
                value = value.Replace(secret, Redacted, StringComparison.Ordinal);
        }

        if (CredentialContent().IsMatch(value))
            return "[credential content omitted]";
        value = BearerToken().Replace(value, $"Bearer {Redacted}");
        value = OpenAiKey().Replace(value, Redacted);
        value = JwtToken().Replace(value, Redacted);
        return value;
    }

    internal static string SanitizeJson(string value, IConfiguration configuration)
    {
        try
        {
            var node = JsonNode.Parse(value);
            return SanitizeNode(node, configuration)?.ToJsonString() ?? "null";
        }
        catch (JsonException)
        {
            return "[invalid JSON omitted]";
        }
    }

    internal static string SanitizeContent(string value, IConfiguration configuration)
    {
        var trimmed = value.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[')
            ? SanitizeJson(value, configuration)
            : Sanitize(value, configuration);
    }

    private static JsonNode? SanitizeNode(JsonNode? node, IConfiguration configuration)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
                obj[property.Key] = SensitiveName().IsMatch(property.Key)
                    ? JsonValue.Create(Redacted)
                    : SanitizeNode(property.Value?.DeepClone(), configuration);
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
                array[i] = SanitizeNode(array[i]?.DeepClone(), configuration);
        }
        else if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
        {
            var trimmed = text.TrimStart();
            if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            {
                try
                {
                    var nested = JsonNode.Parse(text);
                    return JsonValue.Create(SanitizeNode(nested, configuration)?.ToJsonString());
                }
                catch (JsonException)
                {
                    return JsonValue.Create("[invalid JSON omitted]");
                }
            }
            return JsonValue.Create(Sanitize(text, configuration));
        }
        return node;
    }

    [GeneratedRegex(@"(?i)(?:^|[_\-.])(authorization|proxy[_-]?authorization|api[_-]?key|access[_-]?token|refresh[_-]?token|token|password|passwd|secret|credential|cookie|set[_-]?cookie)(?:$|[_\-.])|^(?:apiKey|accessToken|refreshToken|clientSecret)$")]
    private static partial Regex SensitiveName();

    [GeneratedRegex(@"(?i)\b(?:authorization|api[_ -]?key|access[_ -]?token|refresh[_ -]?token|token|password|passwd|secret|credential|cookie)\b\\?[""']?\s*(?:[:=]|\bis\b)")]
    private static partial Regex CredentialContent();

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9._~+/\-=]+")]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9_-]{10,}")]
    private static partial Regex OpenAiKey();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b")]
    private static partial Regex JwtToken();
}
