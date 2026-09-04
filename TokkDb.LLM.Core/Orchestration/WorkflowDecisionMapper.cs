namespace TokkDb.LLM.Core.Orchestration;

/// <summary>
/// Shared translation between agent-authored user interaction requests and
/// application-level decision requests.
/// </summary>
public static class WorkflowDecisionMapper
{
    public static UserDecisionRequest ToDecisionRequest(
        string operationId,
        UserInteractionRequest interaction,
        string title = "Workflow requires your decision")
    {
        ArgumentNullException.ThrowIfNull(interaction);

        var actions = interaction.Actions
            .Select(action => new WorkflowAction
            {
                ActionId = action.Id,
                Title = string.IsNullOrWhiteSpace(action.Title) ? action.Id : action.Title,
                Description = action.Description,
                Decision = MapDecision(action)
            })
            .ToArray();

        return new UserDecisionRequest
        {
            RequestId = interaction.RequestId,
            OperationId = operationId,
            Title = title,
            Message = interaction.Message,
            AvailableActions = actions
        };
    }

    public static WorkflowDecision MapDecision(UserAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var id = action.Id ?? string.Empty;
        var title = action.Title ?? string.Empty;

        if (ContainsAny(id, title, "reject", "decline", "deny", "cancel", "stop", "no"))
        {
            return WorkflowDecision.Reject;
        }

        if (ContainsAny(id, title, "instruction", "instructions", "provide", "clarify", "edit"))
        {
            return WorkflowDecision.ProvideInstructions;
        }

        return WorkflowDecision.Approve;
    }

    public static UserAction? SelectAction(UserDecisionRequest request, WorkflowDecision decision, string? actionId)
    {
        ArgumentNullException.ThrowIfNull(request);

        var match = !string.IsNullOrWhiteSpace(actionId)
            ? request.AvailableActions.FirstOrDefault(action =>
                string.Equals(action.ActionId, actionId, StringComparison.OrdinalIgnoreCase))
            : null;

        match ??= request.AvailableActions.FirstOrDefault(action => action.Decision == decision);
        match ??= request.AvailableActions.FirstOrDefault();

        return match is null
            ? null
            : new UserAction(match.ActionId, match.Title, match.Description);
    }

    private static bool ContainsAny(string id, string title, params string[] tokens)
    {
        return tokens.Any(token =>
            id.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            title.Contains(token, StringComparison.OrdinalIgnoreCase));
    }
}
