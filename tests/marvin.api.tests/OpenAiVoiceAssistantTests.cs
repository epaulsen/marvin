using Marvin.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Chat;
using Xunit;

namespace Marvin.Api.Tests;

public sealed class OpenAiVoiceAssistantTests
{
    [Fact]
    public async Task HistoryIsPerConnectionBoundedAndExpires()
    {
        var clock = new TestClock();
        var firstClient = new TestChatClient((_, _) => Result("Svar"));
        var first = CreateAssistant(firstClient, new TestToolProvider(), clock);
        for (var i = 0; i < 8; i++)
            await first.ReplyAsync($"Spørsmål {i}", TestContext.Current.CancellationToken);

        var context = firstClient.Requests[^1];
        Assert.Equal(14, context.Count);
        Assert.Equal("Spørsmål 1", ((UserChatMessage)context[1]).Content[0].Text);
        Assert.Equal("Spørsmål 7", ((UserChatMessage)context[^1]).Content[0].Text);

        var secondClient = new TestChatClient((_, _) => Result("Svar"));
        var second = CreateAssistant(secondClient, new TestToolProvider(), clock);
        await second.ReplyAsync("Annen enhet", TestContext.Current.CancellationToken);
        Assert.Equal(2, secondClient.Requests[0].Count);

        clock.Advance(TimeSpan.FromMinutes(6));
        await first.ReplyAsync("Ny samtale", TestContext.Current.CancellationToken);
        Assert.Equal(2, firstClient.Requests[^1].Count);
    }

    [Fact]
    public async Task ExecutesToolCallsAndPassesResultToModel()
    {
        var toolSession = new TestToolSession();
        var client = new TestChatClient((_, index) => index == 0
            ? ToolResult("turn_on", """{"name":"Kjøkken"}""")
            : Result("Lyset er på."));
        var assistant = CreateAssistant(client, new TestToolProvider(toolSession), new TestClock());

        Assert.Equal("Lyset er på.", await assistant.ReplyAsync("Slå på lyset", TestContext.Current.CancellationToken));
        Assert.Equal(("turn_on", """{"name":"Kjøkken"}"""), toolSession.Call);
        Assert.Single(client.ToolSets[0]);
        var toolMessage = Assert.IsType<ToolChatMessage>(client.Requests[1][^1]);
        Assert.Equal("ok", toolMessage.Content[0].Text);
        Assert.True(toolSession.Disposed);
    }

    [Fact]
    public async Task FailedTurnIsNotAddedToHistory()
    {
        var client = new TestChatClient((_, index) => index == 0
            ? Result("", ChatFinishReason.Length)
            : Result("Svar"));
        var assistant = CreateAssistant(client, new TestToolProvider(), new TestClock());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            assistant.ReplyAsync("Feilet", TestContext.Current.CancellationToken));
        await assistant.ReplyAsync("Neste", TestContext.Current.CancellationToken);
        Assert.Equal(2, client.Requests[1].Count);
    }

    [Fact]
    public async Task MissingApiKeyPreservesEchoWithoutCallingOpenAi()
    {
        var client = new TestChatClient((_, _) => throw new InvalidOperationException());
        var assistant = new OpenAiVoiceAssistant(client, new TestToolProvider(), new ConfigurationBuilder().Build(),
            new TestClock(), NullLogger<OpenAiVoiceAssistant>.Instance);
        Assert.Equal("Hei", await assistant.ReplyAsync("Hei", TestContext.Current.CancellationToken));
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task CancelledTurnDoesNotEnterHistory()
    {
        var cancellation = new CancellationTokenSource();
        var client = new TestChatClient((_, index) =>
        {
            if (index == 0)
                cancellation.Cancel();
            return Result("Svar");
        });
        var assistant = CreateAssistant(client, new TestToolProvider(), new TestClock());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => assistant.ReplyAsync("Avbrutt", cancellation.Token));
        await assistant.ReplyAsync("Neste", TestContext.Current.CancellationToken);
        Assert.Equal(2, client.Requests[1].Count);
    }

    [Fact]
    public async Task InterruptedOldTurnCannotOverwriteNewConversation()
    {
        var client = new DelayedChatClient();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["OPENAI_API_KEY"] = "test-key" }).Build();
        var assistant = new OpenAiVoiceAssistant(client, new TestToolProvider(), configuration,
            new TestClock(), NullLogger<OpenAiVoiceAssistant>.Instance);
        using var cancellation = new CancellationTokenSource();
        var oldTurn = assistant.ReplyAsync("Gammel", cancellation.Token);
        await client.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await assistant.ReplyAsync("Ny", TestContext.Current.CancellationToken);
        client.Release.TrySetResult(Result("Gammelt svar"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldTurn);
        await assistant.ReplyAsync("Neste", TestContext.Current.CancellationToken);
        var context = client.Requests[^1];
        Assert.Equal(4, context.Count);
        Assert.Equal("Ny", ((UserChatMessage)context[1]).Content[0].Text);
    }

    private static OpenAiVoiceAssistant CreateAssistant(TestChatClient client, TestToolProvider tools, TimeProvider clock)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["OPENAI_API_KEY"] = "test-key" }).Build();
        return new OpenAiVoiceAssistant(client, tools, configuration, clock, NullLogger<OpenAiVoiceAssistant>.Instance);
    }

#pragma warning disable OPENAI001
    private static ChatCompletion Result(string text, ChatFinishReason reason = ChatFinishReason.Stop) =>
        OpenAIChatModelFactory.ChatCompletion(finishReason: reason, content: new ChatMessageContent(text), role: ChatMessageRole.Assistant);

    private static ChatCompletion ToolResult(string name, string arguments) =>
        OpenAIChatModelFactory.ChatCompletion(finishReason: ChatFinishReason.ToolCalls,
            content: new ChatMessageContent(), role: ChatMessageRole.Assistant,
            toolCalls: [ChatToolCall.CreateFunctionToolCall("call-1", name, BinaryData.FromString(arguments))]);
#pragma warning restore OPENAI001

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }

    private sealed class TestChatClient(Func<IReadOnlyList<ChatMessage>, int, ChatCompletion> complete) : IChatCompletionClient
    {
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
        public List<IReadOnlyList<ChatTool>> ToolSets { get; } = [];
        public Task<ChatCompletion> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools,
            CancellationToken cancellationToken)
        {
            Requests.Add(messages.ToArray());
            ToolSets.Add(tools.ToArray());
            return Task.FromResult(complete(messages, Requests.Count - 1));
        }
    }

    private sealed class TestToolProvider(IAgentToolSession? session = null) : IAgentToolProvider
    {
        public Task<IAgentToolSession?> OpenAsync(CancellationToken cancellationToken) => Task.FromResult(session);
    }

    private sealed class DelayedChatClient : IChatCompletionClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ChatCompletion> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public Task<ChatCompletion> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools,
            CancellationToken cancellationToken)
        {
            Requests.Add(messages.ToArray());
            if (Requests.Count == 1)
            {
                Started.TrySetResult();
                return Release.Task;
            }
            return Task.FromResult(Result("Svar"));
        }
    }

    private sealed class TestToolSession : IAgentToolSession
    {
        public (string Name, string Arguments) Call { get; private set; }
        public bool Disposed { get; private set; }
        public IReadOnlyList<ChatTool> Tools { get; } = [ChatTool.CreateFunctionTool("turn_on", "Turn on")];
        public Task<string> CallAsync(string name, string arguments, CancellationToken cancellationToken)
        {
            Call = (name, arguments);
            return Task.FromResult("ok");
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
