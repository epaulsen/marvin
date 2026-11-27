using System.Net.Http.Headers;
using Marvin.Api.Chat;
using Marvin.Api.Configuration;
using Marvin.Api.Skills;
using Marvin.Api.Speech;
using Marvin.Api.Telemetry;
using Marvin.Api.Tools;
using Marvin.Api.Voice;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);
if (builder.Environment.IsDevelopment())
    builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Configuration.AddEnvironmentVariables().AddCommandLine(args);

builder.Services.AddOptions<MarvinOptions>().BindConfiguration("");
// Logging providers must be selected before the service provider is built.
var startupOptions = Options.Create(new MarvinOptions());
builder.Configuration.Bind(startupOptions.Value);

if (!string.IsNullOrWhiteSpace(startupOptions.Value.OtlpEndpoint))
{
    builder.Logging.AddOpenTelemetry(options =>
    {
        options.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("marvin"));
        options.IncludeFormattedMessage = true;
        options.IncludeScopes = true;
        options.ParseStateValues = true;
        options.AddOtlpExporter();
    });
    builder.Services.AddSingleton(_ => Sdk.CreateTracerProviderBuilder()
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("marvin"))
        .AddSource(ChatTelemetry.SourceName)
        .AddOtlpExporter()
        .Build());
}

builder.Services.AddHealthChecks();
builder.Services.AddSingleton<IVoiceActivityDetectorFactory, SileroVadModel>();
builder.Services.AddHttpClient<IVoiceTranscriber, OpperTranscriber>((services, client) =>
{
    client.BaseAddress = new Uri("https://api.opper.ai/");
    var token = services.GetRequiredService<IOptions<MarvinOptions>>().Value.Opper;
    if (!string.IsNullOrWhiteSpace(token))
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
});
builder.Services.AddHttpClient<IVoiceSynthesizer, HomeAssistantSynthesizer>((services, client) =>
{
    var config = services.GetRequiredService<IOptions<MarvinOptions>>().Value;
    if (Uri.TryCreate(config.HomeAssistantUrl, UriKind.Absolute, out var baseUri)
        && (baseUri.Scheme == Uri.UriSchemeHttp || baseUri.Scheme == Uri.UriSchemeHttps)
        && string.IsNullOrEmpty(baseUri.UserInfo) && baseUri.AbsolutePath == "/"
        && string.IsNullOrEmpty(baseUri.Query) && string.IsNullOrEmpty(baseUri.Fragment))
        client.BaseAddress = baseUri;
    if (!string.IsNullOrWhiteSpace(config.HomeAssistantToken))
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.HomeAssistantToken);
})
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
    .RemoveAllLoggers();
builder.Services.AddHttpClient(HomeAssistantSynthesizer.AudioClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
    .RemoveAllLoggers();
builder.Services.AddHttpClient("home-assistant-mcp")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
    .RemoveAllLoggers();
builder.Services.AddScoped<IChatCompletionClient, OpenAiChatCompletionClient>();
builder.Services.AddSingleton<ChatTelemetry>();
builder.Services.AddSingleton<AskUserTool>();
builder.Services.AddSingleton<LoadSkillTool>();
builder.Services.AddScoped<ToolCallExecutor>();
builder.Services.AddScoped<AgentToolDispatcher>();
builder.Services.AddScoped<IAgentToolProvider, HomeAssistantToolProvider>();
builder.Services.AddSingleton<AgentSkillRegistry>();
builder.Services.AddScoped<IVoiceAssistant, OpenAiVoiceAssistant>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IVoiceTurnProcessor, TranscribingVoiceTurnProcessor>();
builder.Services.AddScoped<VoiceSocket>();

var app = builder.Build();
if (!string.IsNullOrWhiteSpace(startupOptions.Value.OtlpEndpoint))
    _ = app.Services.GetRequiredService<TracerProvider>();
app.Logger.LogInformation("Starting in {Environment} environment; content root: {ContentRoot}",
    app.Environment.EnvironmentName, app.Environment.ContentRootPath);
var config = app.Services.GetRequiredService<IOptions<MarvinOptions>>().Value;
if (string.IsNullOrWhiteSpace(config.Opper))
    app.Logger.LogWarning("OPPER_API_KEY is not configured; transcription is disabled");
else
    app.Logger.LogInformation("Opper transcription is configured");
if (!HomeAssistantSynthesizer.IsConfigured(config))
    app.Logger.LogWarning("Home Assistant TTS is not configured; voice responses are disabled");
if (string.IsNullOrWhiteSpace(config.OpenAi))
    app.Logger.LogWarning("OPENAI_API_KEY is not configured; transcripts will be echoed");
_ = app.Services.GetRequiredService<IVoiceActivityDetectorFactory>();
app.Logger.LogInformation("Voice activity detector initialized");
app.UseWebSockets();
app.MapHealthChecks("/health");
app.MapGet("/voice", (HttpContext context, VoiceSocket voiceSocket) => voiceSocket.ReceiveAsync(context));
app.Run();

public partial class Program { }
