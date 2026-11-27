using System.Text.Json;
using OpenAI.Chat;

namespace Marvin.Api.Tools;

internal sealed class AskUserTool
{
    internal const string Name = "ask_user";
    internal ChatTool Definition { get; } = ChatTool.CreateFunctionTool(Name,
        "Ask the user for information needed to continue. The question is spoken and the microphone opens for a reply.",
        BinaryData.FromString("""
            {"type":"object","properties":{"question":{"type":"string","description":"The short question to speak to the user."}},"required":["question"],"additionalProperties":false}
            """));

    internal AgentToolResult Execute(string arguments)
    {
        if (arguments.Length > 32_768)
            throw new InvalidDataException("OpenAI requested an oversized clarification.");
        try
        {
            using var json = JsonDocument.Parse(arguments);
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("question", out var question)
                && question.ValueKind == JsonValueKind.String
                && question.GetString()?.Trim() is { Length: > 0 and <= 2000 } text)
                return new AgentToolResult(text);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("OpenAI returned invalid clarification arguments.", ex);
        }
        throw new InvalidDataException("OpenAI returned an invalid clarification question.");
    }
}
