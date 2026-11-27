namespace Marvin.Api.Configuration;

internal sealed class MarvinOptions
{
    public bool LogTranscripts { get; set; }
    public bool LogChatContent { get; set; } = true;
}
