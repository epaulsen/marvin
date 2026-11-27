namespace Marvin.Api.Configuration;

internal sealed class MarvinOptions
{
    public bool LogTranscripts { get; set; }
    public bool LogChatContent { get; set; } = true;
    public string Model { get; set; } = "gpt-6-luna";
}
