using Marvin.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<IVoiceActivityDetectorFactory, SileroVadModel>();
builder.Services.AddSingleton<IVoiceTurnProcessor, NoOpVoiceTurnProcessor>();
builder.Services.AddScoped<VoiceSocket>();

var app = builder.Build();
_ = app.Services.GetRequiredService<IVoiceActivityDetectorFactory>();
app.UseWebSockets();
app.MapHealthChecks("/health");
app.MapGet("/voice", (HttpContext context, VoiceSocket voiceSocket) => voiceSocket.ReceiveAsync(context));
app.Run();

public partial class Program { }
