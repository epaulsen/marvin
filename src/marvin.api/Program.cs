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

builder.Services.AddOptions<ApiKeyOptions>().BindConfiguration("");
builder.Services.AddOptions<HomeAssistantOptions>().BindConfiguration("HomeAssistant");
builder.Services.AddOptions<MarvinOptions>().BindConfiguration("Marvin").BindConfiguration("OpenAI");
builder.Services.AddOptions<SkillOptions>().BindConfiguration("Skills");
builder.Services.AddSingleton<IOptions<ApiKeyOptions>, ReloadingOptions<ApiKeyOptions>>();
builder.Services.AddSingleton<IOptions<HomeAssistantOptions>, ReloadingOptions<HomeAssistantOptions>>();
builder.Services.AddSingleton<IOptions<MarvinOptions>, ReloadingOptions<MarvinOptions>>();
builder.Services.AddSingleton<IOptions<SkillOptions>, ReloadingOptions<SkillOptions>>();
var telemetryOptions = Options.Create(new TelemetryOptions());
builder.Configuration.Bind(telemetryOptions.Value);
builder.Services.AddSingleton<IOptions<TelemetryOptions>>(telemetryOptions);

if (!string.IsNullOrWhiteSpace(telemetryOptions.Value.Endpoint))
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
builder.Services.AddHttpClient<IVoiceTranscriber, OpperTranscriber>(client =>
    client.BaseAddress = new Uri("https://api.opper.ai/"));
builder.Services.AddHttpClient<IVoiceSynthesizer, HomeAssistantSynthesizer>()
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
if (!string.IsNullOrWhiteSpace(app.Services.GetRequiredService<IOptions<TelemetryOptions>>().Value.Endpoint))
    _ = app.Services.GetRequiredService<TracerProvider>();
app.Logger.LogInformation("Starting in {Environment} environment; content root: {ContentRoot}",
    app.Environment.EnvironmentName, app.Environment.ContentRootPath);
var apiKeys = app.Services.GetRequiredService<IOptions<ApiKeyOptions>>().Value;
if (string.IsNullOrWhiteSpace(apiKeys.Opper))
    app.Logger.LogWarning("OPPER_API_KEY is not configured; transcription is disabled");
else
    app.Logger.LogInformation("Opper transcription is configured");
if (!HomeAssistantSynthesizer.IsConfigured(app.Services.GetRequiredService<IOptions<HomeAssistantOptions>>().Value))
    app.Logger.LogWarning("Home Assistant TTS is not configured; voice responses are disabled");
if (string.IsNullOrWhiteSpace(apiKeys.OpenAi))
    app.Logger.LogWarning("OPENAI_API_KEY is not configured; transcripts will be echoed");
_ = app.Services.GetRequiredService<IVoiceActivityDetectorFactory>();
app.Logger.LogInformation("Voice activity detector initialized");
app.UseWebSockets();
app.MapHealthChecks("/health");
app.MapGet("/voice", (HttpContext context, VoiceSocket voiceSocket) => voiceSocket.ReceiveAsync(context));
app.Run();

public partial class Program { }
