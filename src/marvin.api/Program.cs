using Marvin.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<IVoiceActivityDetectorFactory, SileroVadModel>();
builder.Services.AddHttpClient<IVoiceTranscriber, OpperTranscriber>(client =>
    client.BaseAddress = new Uri("https://api.opper.ai/"));
builder.Services.AddScoped<IVoiceTurnProcessor, TranscribingVoiceTurnProcessor>();
builder.Services.AddScoped<VoiceSocket>();

var app = builder.Build();
if (string.IsNullOrWhiteSpace(app.Configuration["OPPER_API_KEY"]))
    app.Logger.LogWarning("OPPER_API_KEY is not configured; transcription is disabled");
_ = app.Services.GetRequiredService<IVoiceActivityDetectorFactory>();
app.UseWebSockets();
app.MapHealthChecks("/health");
app.MapGet("/voice", (HttpContext context, VoiceSocket voiceSocket) => voiceSocket.ReceiveAsync(context));
app.Run();

public partial class Program { }
