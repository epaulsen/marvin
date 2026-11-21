using System.Text.Json;
using Marvin.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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

        Assert.Equal("Lyset er på.", (await assistant.ReplyAsync("Slå på lyset", TestContext.Current.CancellationToken)).Text);
        Assert.Equal(("turn_on", """{"name":"Kjøkken"}"""), toolSession.Call);
        Assert.Equal(2, client.ToolSets[0].Count);
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
        Assert.Equal(new VoiceAssistantResponse("Hei"), await assistant.ReplyAsync("Hei", TestContext.Current.CancellationToken));
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task AskUserSpeaksQuestionAndKeepsItInConversationHistory()
    {
        var client = new TestChatClient((_, index) => index == 0
            ? ToolResult("ask_user", """{"question":"Hvilket rom?"}""")
            : Result("Lyset på kjøkkenet er på."));
        var assistant = CreateAssistant(client, new TestToolProvider(), new TestClock());

        var question = await assistant.ReplyAsync("Slå på lyset", TestContext.Current.CancellationToken);
        Assert.Equal(new VoiceAssistantResponse("Hvilket rom?", true), question);
        Assert.Single(client.ToolSets[0]);
        Assert.Single(client.Requests);

        var reply = await assistant.ReplyAsync("Kjøkkenet", TestContext.Current.CancellationToken);
        Assert.Equal(new VoiceAssistantResponse("Lyset på kjøkkenet er på."), reply);
        Assert.Equal("Hvilket rom?", Assert.IsType<AssistantChatMessage>(client.Requests[1][2]).Content[0].Text);
        Assert.Equal("Kjøkkenet", Assert.IsType<UserChatMessage>(client.Requests[1][3]).Content[0].Text);
    }

    [Fact]
    public async Task StructuredReplyCanRequestFollowUpAfterToolFailure()
    {
        var toolSession = new TestToolSession("""{"isError":true}""", isError: true);
        var client = new TestChatClient((_, index) => index == 0
            ? ToolResult("turn_on", """{"name":"Stua"}""")
            : Result("Jeg fant ingen varmepumpe. Hva heter den?", requestFollowUp: true));
        var assistant = CreateAssistant(client, new TestToolProvider(toolSession), new TestClock());

        var response = await assistant.ReplyAsync("Sett temperaturen", TestContext.Current.CancellationToken);
        Assert.Equal(new VoiceAssistantResponse("Jeg fant ingen varmepumpe. Hva heter den?", true), response);
        Assert.Equal("""{"isError":true}""", Assert.IsType<ToolChatMessage>(client.Requests[1][^1]).Content[0].Text);
    }

    [Fact]
    public async Task PunctuationAloneDoesNotRequestFollowUp()
    {
        var client = new TestChatClient((_, _) => Result("Ikke sant?"));
        var assistant = CreateAssistant(client, new TestToolProvider(), new TestClock());
        Assert.Equal(new VoiceAssistantResponse("Ikke sant?"),
            await assistant.ReplyAsync("Si noe", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"text":"Svar","needs_follow_up":"true"}""")]
    [InlineData("""{"text":" ","needs_follow_up":true}""")]
    [InlineData("plain text")]
    public async Task InvalidStructuredResponseIsRejected(string content)
    {
        var client = new TestChatClient((_, _) => RawResult(content));
        var assistant = CreateAssistant(client, new TestToolProvider(), new TestClock());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            assistant.ReplyAsync("Spørsmål", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ToolCallsLogNamesStatusAndDurationWithoutSensitiveContent()
    {
        var logger = new CapturingLogger<OpenAiVoiceAssistant>();
        var client = new TestChatClient((_, index) => index == 0
            ? ToolResult("turn_on", """{"name":"private argument"}""")
            : ToolResult("ask_user", """{"question":"private question"}"""));
        var session = new TestToolSession("private result", isError: true);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["OPENAI_API_KEY"] = "test-key" }).Build();
        var assistant = new OpenAiVoiceAssistant(client, new TestToolProvider(session), configuration,
            new TestClock(), logger);

        await assistant.ReplyAsync("Spørsmål", TestContext.Current.CancellationToken);

        Assert.Contains(logger.Messages, entry => entry.Contains("Tool call started: turn_on"));
        Assert.Contains(logger.Messages, entry => entry.Contains("turn_on; status: reported error; duration:"));
        Assert.Contains(logger.Messages, entry => entry.Contains("Tool call started: ask_user"));
        Assert.Contains(logger.Messages, entry => entry.Contains("ask_user; status: completed; duration:"));
        Assert.DoesNotContain(logger.Messages, entry => entry.Contains("private", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedToolCallIsLoggedWithoutItsArguments()
    {
        var logger = new CapturingLogger<OpenAiVoiceAssistant>();
        var client = new TestChatClient((_, _) => ToolResult("ask_user", "private invalid json"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["OPENAI_API_KEY"] = "test-key" }).Build();
        var assistant = new OpenAiVoiceAssistant(client, new TestToolProvider(), configuration,
            new TestClock(), logger);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            assistant.ReplyAsync("Spørsmål", TestContext.Current.CancellationToken));

        Assert.Contains(logger.Messages, entry => entry.Contains("Tool call failed: ask_user; duration:"));
        Assert.DoesNotContain(logger.Messages, entry => entry.Contains("private", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"question":"  "}""")]
    [InlineData("""{"question":42}""")]
    [InlineData("invalid json")]
    public async Task InvalidClarificationDoesNotEnterHistory(string arguments)
    {
        var client = new TestChatClient((_, index) => index == 0
            ? ToolResult("ask_user", arguments)
            : Result("Svar"));
        var assistant = CreateAssistant(client, new TestToolProvider(), new TestClock());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            assistant.ReplyAsync("Spørsmål", TestContext.Current.CancellationToken));
        await assistant.ReplyAsync("Neste", TestContext.Current.CancellationToken);
        Assert.Equal(2, client.Requests[1].Count);
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
    private static ChatCompletion Result(string text, ChatFinishReason reason = ChatFinishReason.Stop, bool requestFollowUp = false) =>
        OpenAIChatModelFactory.ChatCompletion(finishReason: reason,
            content: new ChatMessageContent(JsonSerializer.Serialize(new { text, needs_follow_up = requestFollowUp })),
            role: ChatMessageRole.Assistant);

    private static ChatCompletion RawResult(string text) =>
        OpenAIChatModelFactory.ChatCompletion(finishReason: ChatFinishReason.Stop, content: new ChatMessageContent(text),
            role: ChatMessageRole.Assistant);

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

    private sealed class TestToolSession(string content = "ok", bool isError = false) : IAgentToolSession
    {
        public (string Name, string Arguments) Call { get; private set; }
        public bool Disposed { get; private set; }
        public IReadOnlyList<ChatTool> Tools { get; } = [ChatTool.CreateFunctionTool("turn_on", "Turn on")];
        public Task<AgentToolResult> CallAsync(string name, string arguments, CancellationToken cancellationToken)
        {
            Call = (name, arguments);
            return Task.FromResult(new AgentToolResult(content, isError));
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
