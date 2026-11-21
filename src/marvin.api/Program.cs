using Marvin.Api;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);
if (builder.Environment.IsDevelopment())
    builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Configuration.AddEnvironmentVariables().AddCommandLine(args);

if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Logging.AddOpenTelemetry(options =>
    {
        options.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("marvin"));
        options.IncludeFormattedMessage = true;
        options.IncludeScopes = true;
        options.ParseStateValues = true;
        options.AddOtlpExporter();
    });
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
builder.Services.AddScoped<IAgentToolProvider, HomeAssistantToolProvider>();
builder.Services.AddScoped<IVoiceAssistant, OpenAiVoiceAssistant>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IVoiceTurnProcessor, TranscribingVoiceTurnProcessor>();
builder.Services.AddScoped<VoiceSocket>();

var app = builder.Build();
app.Logger.LogInformation("Starting in {Environment} environment; content root: {ContentRoot}",
    app.Environment.EnvironmentName, app.Environment.ContentRootPath);
if (string.IsNullOrWhiteSpace(app.Configuration["OPPER_API_KEY"]))
    app.Logger.LogWarning("OPPER_API_KEY is not configured; transcription is disabled");
else
    app.Logger.LogInformation("Opper transcription is configured");
if (!HomeAssistantSynthesizer.IsConfigured(app.Configuration))
    app.Logger.LogWarning("Home Assistant TTS is not configured; voice responses are disabled");
if (string.IsNullOrWhiteSpace(app.Configuration["OPENAI_API_KEY"]))
    app.Logger.LogWarning("OPENAI_API_KEY is not configured; transcripts will be echoed");
_ = app.Services.GetRequiredService<IVoiceActivityDetectorFactory>();
app.Logger.LogInformation("Voice activity detector initialized");
app.UseWebSockets();
app.MapHealthChecks("/health");
app.MapGet("/voice", (HttpContext context, VoiceSocket voiceSocket) => voiceSocket.ReceiveAsync(context));
app.Run();

public partial class Program { }
