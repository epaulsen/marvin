using System.Diagnostics;
using Marvin.Api.Configuration;
using Marvin.Api.Chat;
using Marvin.Api.Telemetry;
using Microsoft.Extensions.Options;

namespace Marvin.Api.Tools;

internal sealed class ToolCallExecutor(
    IOptions<MarvinOptions> options,
    ChatTelemetry telemetry,
    ILogger<OpenAiVoiceAssistant> logger)
{
    internal async Task<AgentToolResult> ExecuteAsync(string name, string arguments, int round,
        Func<Task<AgentToolResult>> execute, CancellationToken cancellationToken)
    {
        using var activity = ChatTelemetry.Source.StartActivity("tool " + name);
        activity?.SetTag("marvin.chat.round", round);
        activity?.SetTag("marvin.tool.name", name);
        logger.LogInformation("Tool call started: {ToolName}", name);
        if (options.Value.LogChatContent)
        {
            var sanitized = telemetry.SanitizeJson(arguments);
            activity?.SetTag("marvin.tool.arguments", sanitized);
            logger.LogInformation("Tool call arguments: {ToolName}; {Arguments}", name, sanitized);
        }
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await execute();
            if (options.Value.LogChatContent)
            {
                var sanitized = telemetry.SanitizeContent(result.Content);
                activity?.SetTag("marvin.tool.result", sanitized);
                logger.LogInformation("Tool call result: {ToolName}; {Result}", name, sanitized);
            }
            activity?.SetStatus(result.IsError ? ActivityStatusCode.Error : ActivityStatusCode.Ok,
                result.IsError ? "reported error" : null);
            logger.LogInformation("Tool call finished: {ToolName}; status: {Status}; duration: {ElapsedMs:F0} ms",
                name, result.IsError ? "reported error" : "completed", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
            logger.LogInformation("Tool call cancelled: {ToolName}; duration: {ElapsedMs:F0} ms",
                name, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
        catch
        {
            activity?.SetStatus(ActivityStatusCode.Error, "failed");
            logger.LogWarning("Tool call failed: {ToolName}; duration: {ElapsedMs:F0} ms",
                name, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }
}
