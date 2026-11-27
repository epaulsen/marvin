using Marvin.Api.Configuration;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace Marvin.Api.Chat;

internal sealed class OpenAiChatCompletionClient(IOptions<MarvinOptions> settings) : IChatCompletionClient
{
    private static readonly ChatResponseFormat VoiceResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
        "voice_response",
        BinaryData.FromString("""
            {"type":"object","properties":{"text":{"type":"string","description":"The short response to speak."},"needs_follow_up":{"type":"boolean","description":"True if the response asks the user to provide information or make a choice before continuing; false for completed answers and rhetorical questions."}},"required":["text","needs_follow_up"],"additionalProperties":false}
            """),
        jsonSchemaIsStrict: true);
    private ChatClient? client;

    internal OpenAiChatCompletionClient(IOptions<MarvinOptions> settings, ChatClient client) : this(settings) =>
        this.client = client;

    public async Task<ChatCompletion> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools,
        CancellationToken cancellationToken)
    {
        client ??= new ChatClient(settings.Value.Model, settings.Value.OpenAi!);
        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = 200,
            AllowParallelToolCalls = false,
            StoredOutputEnabled = false,
            ResponseFormat = VoiceResponseFormat
        };
#pragma warning disable OPENAI001
        options.ReasoningEffortLevel = ChatReasoningEffortLevel.None;
#pragma warning restore OPENAI001
        foreach (var tool in tools)
            options.Tools.Add(tool);
        return await client.CompleteChatAsync(messages, options, cancellationToken);
    }
}
