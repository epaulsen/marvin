using Marvin.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

var app = builder.Build();
app.UseWebSockets();
app.MapHealthChecks("/health");
app.MapGet("/voice", (HttpContext context) => VoiceSocket.ReceiveAsync(context, app.Logger));
app.Run();

public partial class Program { }
