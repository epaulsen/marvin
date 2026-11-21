using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Marvin.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;
using OpenAI.Chat;
using Xunit;

namespace Marvin.Api.Tests;

public sealed class OpenAiChatCompletionClientTests
{
    [Fact]
    public async Task SendsVoiceOptimizedRequestThroughOfficialSdk()
    {
        var handler = new TestHandler(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization.Parameter);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("gpt-6-luna", json.RootElement.GetProperty("model").GetString());
            Assert.Equal("none", json.RootElement.GetProperty("reasoning_effort").GetString());
            Assert.Equal(200, json.RootElement.GetProperty("max_completion_tokens").GetInt32());
            Assert.False(json.RootElement.GetProperty("store").GetBoolean());
            Assert.Single(json.RootElement.GetProperty("tools").EnumerateArray());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"id":"test","object":"chat.completion","created":1700000000,"model":"gpt-6-luna",
                     "choices":[{"index":0,"message":{"role":"assistant","content":"Hei tilbake"},"finish_reason":"stop"}]}
                    """, Encoding.UTF8, "application/json")
            };
        });
        var sdk = new ChatClient("gpt-6-luna", new ApiKeyCredential("test-key"), new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(new HttpClient(handler))
        });
        var client = new OpenAiChatCompletionClient(new ConfigurationBuilder().Build(), sdk);
        var response = await client.CompleteAsync([new UserChatMessage("Hei")],
            [ChatTool.CreateFunctionTool("test_tool", "Test")], TestContext.Current.CancellationToken);
        Assert.Equal("Hei tilbake", response.Content[0].Text);
    }

    [Fact]
    public async Task AskUserToolRoundTripsThroughOfficialSdk()
    {
        var handler = new TestHandler(async (request, cancellationToken) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var function = json.RootElement.GetProperty("tools")[0].GetProperty("function");
            Assert.Equal("ask_user", function.GetProperty("name").GetString());
            Assert.Equal("string", function.GetProperty("parameters").GetProperty("properties")
                .GetProperty("question").GetProperty("type").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"id":"test","object":"chat.completion","created":1700000000,"model":"gpt-6-luna",
                     "choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call-1",
                     "type":"function","function":{"name":"ask_user","arguments":"{\"question\":\"Hvilket rom?\"}"}}]},
                     "finish_reason":"tool_calls"}]}
                    """, Encoding.UTF8, "application/json")
            };
        });
        var sdk = new ChatClient("gpt-6-luna", new ApiKeyCredential("test-key"), new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(new HttpClient(handler))
        });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["OPENAI_API_KEY"] = "test-key" }).Build();
        var assistant = new OpenAiVoiceAssistant(new OpenAiChatCompletionClient(configuration, sdk),
            new DisabledToolProvider(), configuration, TimeProvider.System, NullLogger<OpenAiVoiceAssistant>.Instance);

        Assert.Equal(new VoiceAssistantResponse("Hvilket rom?", true),
            await assistant.ReplyAsync("Slå på lyset", TestContext.Current.CancellationToken));
    }

    private sealed class DisabledToolProvider : IAgentToolProvider
    {
        public Task<IAgentToolSession?> OpenAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IAgentToolSession?>(null);
    }

    private sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
