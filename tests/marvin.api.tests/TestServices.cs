using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace Marvin.Api.Tests;

internal static class TestServices
{
    internal static IOptions<MarvinOptions> Marvin(IConfiguration configuration) =>
        Options.Create(configuration.Get<MarvinOptions>() ?? new MarvinOptions());

    internal static ChatTelemetry Telemetry(IConfiguration configuration) =>
        new(Marvin(configuration));

    internal static OpenAiChatCompletionClient ChatClient(IConfiguration configuration, ChatClient client) =>
        new(Marvin(configuration), client);

    internal static OpenAiVoiceAssistant Assistant(IChatCompletionClient client, IAgentToolProvider provider,
        AgentSkillRegistry skills, IConfiguration configuration, TimeProvider clock, ILogger<OpenAiVoiceAssistant> logger)
    {
        var options = Marvin(configuration);
        var telemetry = Telemetry(configuration);
        var dispatcher = new AgentToolDispatcher(new AskUserTool(), new LoadSkillTool(),
            new ToolCallExecutor(options, telemetry, logger));
        return new OpenAiVoiceAssistant(client, provider, skills, options, telemetry,
            dispatcher, clock, logger);
    }

    internal static TranscribingVoiceTurnProcessor Processor(IVoiceTranscriber transcriber, IVoiceSynthesizer synthesizer,
        IVoiceAssistant assistant, IConfiguration configuration, ILogger<TranscribingVoiceTurnProcessor> logger) =>
        new(transcriber, synthesizer, assistant, Marvin(configuration), Telemetry(configuration), logger);
}
