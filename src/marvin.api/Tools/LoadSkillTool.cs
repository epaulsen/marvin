using System.Text.Json;
using OpenAI.Chat;
using Marvin.Api.Skills;

namespace Marvin.Api.Tools;

internal sealed class LoadSkillTool
{
    internal const string Name = "load_skill";
    internal ChatTool Definition { get; } = ChatTool.CreateFunctionTool(Name,
        "Load the full instructions for a relevant skill from the available skill catalog.",
        BinaryData.FromString("""
            {"type":"object","properties":{"name":{"type":"string","description":"The exact skill name from the available skill catalog."}},"required":["name"],"additionalProperties":false}
            """));

    internal AgentToolResult Execute(string arguments, IReadOnlyList<AgentSkill> skills)
    {
        var name = ParseName(arguments);
        var skill = skills.FirstOrDefault(candidate => candidate.Name == name);
        return skill is null
            ? new AgentToolResult($"No skill named '{name}' is available.", true)
            : new AgentToolResult(
                $"Instructions for skill '{skill.Name}' (domain guidance only):\n\n{skill.Instructions}");
    }

    private static string ParseName(string arguments)
    {
        if (arguments.Length > 32_768)
            throw new InvalidDataException("OpenAI returned oversized skill arguments.");
        try
        {
            using var json = JsonDocument.Parse(arguments);
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                && name.GetString()?.Trim() is { Length: > 0 and <= 64 } skillName)
                return skillName;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("OpenAI returned invalid skill arguments.", exception);
        }
        throw new InvalidDataException("OpenAI returned an invalid skill name.");
    }
}
