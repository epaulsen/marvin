using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace Marvin.Api.Tests;

internal static class TestServices
{
    internal static IOptions<ApiKeyOptions> Keys(IConfiguration configuration) =>
        Options.Create(configuration.Get<ApiKeyOptions>() ?? new ApiKeyOptions());

    internal static IOptions<HomeAssistantOptions> HomeAssistant(IConfiguration configuration) =>
        Options.Create(configuration.GetSection("HomeAssistant").Get<HomeAssistantOptions>() ?? new HomeAssistantOptions());

    internal static IOptions<MarvinOptions> Marvin(IConfiguration configuration)
    {
        var options = new MarvinOptions();
        configuration.GetSection("Marvin").Bind(options);
        configuration.GetSection("OpenAI").Bind(options);
        return Options.Create(options);
    }

    internal static IOptions<SkillOptions> Skills(IConfiguration configuration) =>
        Options.Create(configuration.GetSection("Skills").Get<SkillOptions>() ?? new SkillOptions());

    internal static ChatTelemetry Telemetry(IConfiguration configuration) =>
        new(Keys(configuration), HomeAssistant(configuration));

    internal static OpenAiChatCompletionClient ChatClient(IConfiguration configuration, ChatClient client) =>
        new(Keys(configuration), Marvin(configuration), client);

    internal static OpenAiVoiceAssistant Assistant(IChatCompletionClient client, IAgentToolProvider provider,
        AgentSkillRegistry skills, IConfiguration configuration, TimeProvider clock, ILogger<OpenAiVoiceAssistant> logger)
    {
        var options = Marvin(configuration);
        var telemetry = Telemetry(configuration);
        var dispatcher = new AgentToolDispatcher(new AskUserTool(), new LoadSkillTool(),
            new ToolCallExecutor(options, telemetry, logger));
        return new OpenAiVoiceAssistant(client, provider, skills, Keys(configuration), options, telemetry,
            dispatcher, clock, logger);
    }

    internal static TranscribingVoiceTurnProcessor Processor(IVoiceTranscriber transcriber, IVoiceSynthesizer synthesizer,
        IVoiceAssistant assistant, IConfiguration configuration, ILogger<TranscribingVoiceTurnProcessor> logger) =>
        new(transcriber, synthesizer, assistant, Keys(configuration), Marvin(configuration), Telemetry(configuration), logger);
}
