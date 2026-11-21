using Marvin.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);
if (builder.Environment.IsDevelopment())
    builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Configuration.AddEnvironmentVariables().AddCommandLine(args);

builder.Services.AddHealthChecks();
builder.Services.AddSingleton<IVoiceActivityDetectorFactory, SileroVadModel>();
builder.Services.AddHttpClient<IVoiceTranscriber, OpperTranscriber>(client =>
    client.BaseAddress = new Uri("https://api.opper.ai/"));
builder.Services.AddScoped<IVoiceTurnProcessor, TranscribingVoiceTurnProcessor>();
builder.Services.AddScoped<VoiceSocket>();

var app = builder.Build();
app.Logger.LogInformation("Starting in {Environment} environment; content root: {ContentRoot}",
    app.Environment.EnvironmentName, app.Environment.ContentRootPath);
if (string.IsNullOrWhiteSpace(app.Configuration["OPPER_API_KEY"]))
    app.Logger.LogWarning("OPPER_API_KEY is not configured; transcription is disabled");
else
    app.Logger.LogInformation("Opper transcription is configured");
_ = app.Services.GetRequiredService<IVoiceActivityDetectorFactory>();
app.Logger.LogInformation("Voice activity detector initialized");
app.UseWebSockets();
app.MapHealthChecks("/health");
app.MapGet("/voice", (HttpContext context, VoiceSocket voiceSocket) => voiceSocket.ReceiveAsync(context));
app.Run();

public partial class Program { }
