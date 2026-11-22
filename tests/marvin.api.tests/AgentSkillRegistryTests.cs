using Marvin.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Marvin.Api.Tests;

public sealed class AgentSkillRegistryTests
{
    [Fact]
    public void DiscoversValidSkillsAndSkipsInvalidDirectories()
    {
        using var directory = new TemporaryDirectory();
        WriteSkill(directory.Path, "room-temperature", """
            ---
            name: room-temperature
            description: >-
              Use for room temperature questions
              and measured climate values.
            ---

            Use `current_temperature` for the measured value.
            """);
        WriteSkill(directory.Path, "invalid-skill", """
            ---
            name: mismatched-name
            description: Invalid because name does not match directory.
            ---

            Ignore this skill.
            """);
        Directory.CreateDirectory(Path.Combine(directory.Path, "not-a-skill"));

        var registry = CreateRegistry(directory.Path);

        var skill = Assert.Single(registry.Discover());
        Assert.Equal("room-temperature", skill.Name);
        Assert.Equal("Use for room temperature questions and measured climate values.", skill.Description);
        Assert.Contains("current_temperature", skill.Instructions, StringComparison.Ordinal);
    }

    private static AgentSkillRegistry CreateRegistry(string directory)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Skills:Directory"] = directory
        }).Build();
        return new AgentSkillRegistry(configuration, NullLogger<AgentSkillRegistry>.Instance);
    }

    private static void WriteSkill(string root, string name, string content)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), content);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"marvin-skills-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
