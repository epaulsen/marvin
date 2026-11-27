using Marvin.Api.Skills;
using OpenAI.Chat;

namespace Marvin.Api.Tools;

internal sealed class AgentToolDispatcher(AskUserTool askUser, LoadSkillTool loadSkill, ToolCallExecutor executor)
{
    internal List<ChatTool> GetTools(IReadOnlyList<AgentSkill> skills, IAgentToolSession? session)
    {
        var tools = new List<ChatTool> { askUser.Definition };
        if (skills.Count > 0)
            tools.Add(loadSkill.Definition);
        if (session is not null)
            tools.AddRange(session.Tools);
        return tools;
    }

    internal async Task<AgentToolResult?> ExecuteAsync(ChatCompletion completion, IAgentToolSession? session,
        IReadOnlyList<AgentSkill> skills, List<ChatMessage> messages, int round, CancellationToken cancellationToken)
    {
        if (completion.ToolCalls.Count is 0 or > 4)
            throw new InvalidDataException("OpenAI returned an invalid number of tool calls.");
        if (completion.ToolCalls.Any(call => string.IsNullOrEmpty(call.FunctionName)
            || call.FunctionName.Length > 64
            || !call.FunctionName.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
            throw new InvalidDataException("OpenAI returned an invalid tool name.");

        if (completion.ToolCalls.Any(call => call.FunctionName == AskUserTool.Name))
        {
            if (completion.ToolCalls.Count != 1)
                throw new InvalidDataException("OpenAI requested clarification alongside other tool calls.");
            var arguments = completion.ToolCalls[0].FunctionArguments.ToString();
            return await executor.ExecuteAsync(AskUserTool.Name, arguments, round,
                () => Task.FromResult(askUser.Execute(arguments)), cancellationToken);
        }

        if (completion.ToolCalls.Any(call => call.FunctionName == LoadSkillTool.Name))
        {
            if (completion.ToolCalls.Any(call => call.FunctionName != LoadSkillTool.Name))
                throw new InvalidDataException("OpenAI requested a skill alongside other tool calls.");
            messages.Add(new AssistantChatMessage(completion));
            foreach (var call in completion.ToolCalls)
            {
                var arguments = call.FunctionArguments.ToString();
                var result = await executor.ExecuteAsync(LoadSkillTool.Name, arguments, round,
                    () => Task.FromResult(loadSkill.Execute(arguments, skills)), cancellationToken);
                messages.Add(new ToolChatMessage(call.Id, result.Content));
            }
            return null;
        }

        if (session is null)
            throw new InvalidDataException("OpenAI requested an unavailable Home Assistant tool.");

        messages.Add(new AssistantChatMessage(completion));
        foreach (var call in completion.ToolCalls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var arguments = call.FunctionArguments.ToString();
            var result = await executor.ExecuteAsync(call.FunctionName, arguments, round,
                () => session.CallAsync(call.FunctionName, arguments, cancellationToken), cancellationToken);
            messages.Add(new ToolChatMessage(call.Id, result.Content));
        }
        return null;
    }
}
