using Marvin.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<VoiceSocket>();

var app = builder.Build();
app.UseWebSockets();
app.MapHealthChecks("/health");
app.MapGet("/voice", (HttpContext context, VoiceSocket voiceSocket) => voiceSocket.ReceiveAsync(context));
app.Run();

public partial class Program { }
