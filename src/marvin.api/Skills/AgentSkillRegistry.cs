using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using Marvin.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Marvin.Api.Skills;

internal sealed record AgentSkill(string Name, string Description, string Instructions);

internal sealed class AgentSkillRegistry
{
    private const int MaxSkillFileBytes = 128 * 1024;
    private readonly string skillsDirectory;
    private readonly ILogger<AgentSkillRegistry> logger;

    public AgentSkillRegistry(IOptions<SkillOptions> options, ILogger<AgentSkillRegistry> logger)
    {
        var configuredDirectory = options.Value.Directory;
        skillsDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(configuredDirectory)
                ? Path.Combine(AppContext.BaseDirectory, "skills")
                : configuredDirectory,
            AppContext.BaseDirectory);
        this.logger = logger;
        if (!Directory.Exists(skillsDirectory))
            logger.LogWarning("Skills directory is missing: {SkillsDirectory}", skillsDirectory);
    }

    public IReadOnlyList<AgentSkill> Discover()
    {
        if (!Directory.Exists(skillsDirectory))
            return [];

        var skills = new List<AgentSkill>();
        foreach (var skillDirectory in Directory.EnumerateDirectories(skillsDirectory).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(skillDirectory);
            var skillPath = Path.Combine(skillDirectory, "SKILL.md");
            try
            {
                if ((File.GetAttributes(skillDirectory) & FileAttributes.ReparsePoint) != 0
                    || (File.GetAttributes(skillPath) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Skill files must not be symbolic links.");

                if (new FileInfo(skillPath).Length > MaxSkillFileBytes)
                    throw new InvalidDataException($"Skill file exceeds the {MaxSkillFileBytes}-byte limit.");

                skills.Add(ParseSkill(name, File.ReadAllText(skillPath)));
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (Exception exception) when (exception is IOException
                || exception is InvalidDataException
                || exception is UnauthorizedAccessException
                || exception is YamlException)
            {
                logger.LogWarning(exception, "Ignoring invalid skill {SkillName}", name);
            }
        }

        return skills;
    }

    private static AgentSkill ParseSkill(string directoryName, string content)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (lines.Length < 2 || lines[0].TrimStart('\uFEFF') != "---")
            throw new InvalidDataException("Skill must start with YAML frontmatter.");

        var closingDelimiter = Array.FindIndex(lines, 1, line => line == "---");
        if (closingDelimiter < 0)
            throw new InvalidDataException("Skill YAML frontmatter is not closed.");

        var yaml = new YamlStream();
        yaml.Load(new StringReader(string.Join('\n', lines[1..closingDelimiter])));
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode metadata)
            throw new InvalidDataException("Skill frontmatter must be one YAML mapping.");

        var name = GetRequiredScalar(metadata, "name");
        var description = GetRequiredScalar(metadata, "description").Trim();
        if (!IsValidName(name) || !string.Equals(name, directoryName, StringComparison.Ordinal))
            throw new InvalidDataException("Skill name must match its directory and follow Agent Skills naming rules.");
        if (description.Length is 0 or > 1024)
            throw new InvalidDataException("Skill description must contain between 1 and 1024 characters.");

        return new AgentSkill(name, description, string.Join('\n', lines[(closingDelimiter + 1)..]).Trim());
    }

    private static string GetRequiredScalar(YamlMappingNode metadata, string key)
    {
        if (metadata.Children.TryGetValue(new YamlScalarNode(key), out var value)
            && value is YamlScalarNode scalar
            && !string.IsNullOrWhiteSpace(scalar.Value))
            return scalar.Value;

        throw new InvalidDataException($"Skill frontmatter requires a non-empty '{key}' scalar.");
    }

    private static bool IsValidName(string name) =>
        name.Length is > 0 and <= 64
        && name[0] != '-'
        && name[^1] != '-'
        && !name.Contains("--", StringComparison.Ordinal)
        && name.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}
