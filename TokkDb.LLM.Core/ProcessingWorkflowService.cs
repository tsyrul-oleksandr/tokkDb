using Microsoft.Extensions.Logging;
using System.Text;

namespace TokkDb.LLM.Core;

public sealed class ProcessingWorkflowService : IProcessingWorkflowService
{
    private readonly IConversationAgent _conversationAgent;
    private readonly ILogger<ProcessingWorkflowService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ProcessingContext? _context;

    public ProcessingWorkflowService(
        IConversationAgent conversationAgent,
        ILogger<ProcessingWorkflowService> logger)
    {
        _conversationAgent = conversationAgent;
        _logger = logger;
    }

    public ProcessingContext? GetCurrentContext()
    {
        return _context;
    }

    public async Task<ProcessingContext> StartAsync(ConversationRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var context = new ProcessingContext(
                Guid.NewGuid(),
                ProcessingState.Running,
                request,
                request.Message,
                null,
                null,
                null,
                null,
                null,
                now,
                now,
                [$"[{now:O}] Running: Workflow started."],
                BuildOperationContext(request));

            _context = context;

            var response = await _conversationAgent.SendAsync(request, cancellationToken);
            context = Transition(context, ProcessingState.Running, "Evaluating workflow result.", lastAgentResponse: response.Text);

            if (response.UserInteractionRequest is not null)
            {
                context = Transition(
                    context,
                    ProcessingState.WaitingForUser,
                    "Waiting for user decision.",
                    pendingUserInteraction: response.UserInteractionRequest,
                    pendingDecisionRequest: BuildDecisionRequest(context.WorkflowId, response.UserInteractionRequest));
            }
            else
            {
                context = Transition(context, ProcessingState.Completed, "Workflow completed.");
            }

            _context = context;
            return context;
        }
        catch (OperationCanceledException)
        {
            var cancelled = _context is null
                ? CreateCancelled(request, "Workflow cancelled.")
                : Transition(_context, ProcessingState.Cancelled, "Workflow cancelled.");
            _context = cancelled;
            return cancelled;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Workflow start failed.");
            var failed = FailCurrentOrCreate(request, ex.Message);
            _context = failed;
            return failed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProcessingContext> ResumeAsync(
        string operationId,
        WorkflowDecision decision,
        string? additionalInstructions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_context is null)
            {
                throw new InvalidOperationException("No workflow context is available.");
            }

            if (_context.State != ProcessingState.WaitingForUser ||
                _context.PendingUserInteraction is null ||
                _context.PendingDecisionRequest is null)
            {
                throw new InvalidOperationException("Current workflow is not waiting for user input.");
            }

            if (!string.Equals(_context.WorkflowId.ToString("N"), operationId.Trim(), StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(_context.WorkflowId.ToString(), operationId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Workflow '{operationId}' is not the active waiting workflow.");
            }

            var matchingDecisionAction = _context.PendingDecisionRequest.AvailableActions.FirstOrDefault(
                action => action.Decision == decision);
            if (matchingDecisionAction is null)
            {
                throw new InvalidOperationException(
                    $"Decision '{decision}' is not available for operation '{operationId}'.");
            }

            var selectedAction = _context.PendingUserInteraction.Actions.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, matchingDecisionAction.ActionId, StringComparison.OrdinalIgnoreCase))
                ?? _context.PendingUserInteraction.Actions.FirstOrDefault();
            if (selectedAction is null)
            {
                throw new InvalidOperationException(
                    $"No matching user action found for decision '{decision}' on operation '{operationId}'.");
            }

            if (decision == WorkflowDecision.ProvideInstructions && string.IsNullOrWhiteSpace(additionalInstructions))
            {
                var waitingForInstructions = Transition(
                    _context,
                    ProcessingState.WaitingForUser,
                    "Additional instructions are required.",
                    pendingUserInteraction: _context.PendingUserInteraction,
                    pendingDecisionRequest: _context.PendingDecisionRequest,
                    selectedAction: selectedAction);
                _context = waitingForInstructions;
                return waitingForInstructions;
            }

            if (decision == WorkflowDecision.Reject)
            {
                var cancelled = Transition(
                    _context,
                    ProcessingState.Cancelled,
                    $"User rejected action '{selectedAction.Id}'.",
                    selectedAction: selectedAction,
                    pendingUserInteraction: null,
                    pendingDecisionRequest: null,
                    additionalInstructions: additionalInstructions);
                _context = cancelled;
                return cancelled;
            }

            var context = Transition(
                _context,
                ProcessingState.Resuming,
                $"Resuming workflow with decision '{decision}'.",
                selectedAction: selectedAction,
                pendingUserInteraction: null,
                pendingDecisionRequest: null,
                additionalInstructions: additionalInstructions);

            var resumeRequest = _context.ProviderConfiguration with
            {
                Message = BuildResumeMessage(_context, selectedAction, decision, additionalInstructions)
            };

            var response = await _conversationAgent.SendAsync(resumeRequest, cancellationToken);
            context = Transition(
                context,
                ProcessingState.Resuming,
                "Evaluating resumed workflow result.",
                lastAgentResponse: response.Text);

            if (response.UserInteractionRequest is not null)
            {
                context = Transition(
                    context,
                    ProcessingState.WaitingForUser,
                    "Waiting for user decision.",
                    pendingUserInteraction: response.UserInteractionRequest,
                    pendingDecisionRequest: BuildDecisionRequest(context.WorkflowId, response.UserInteractionRequest));
            }
            else
            {
                context = Transition(context, ProcessingState.Completed, "Workflow completed.");
            }

            _context = context;
            return context;
        }
        catch (OperationCanceledException)
        {
            if (_context is null)
            {
                throw;
            }

            var cancelled = Transition(_context, ProcessingState.Cancelled, "Workflow cancelled.");
            _context = cancelled;
            return cancelled;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Workflow resume failed.");
            if (_context is null)
            {
                throw;
            }

            var failed = Transition(_context, ProcessingState.Failed, "Workflow failed.", failureReason: ex.Message);
            _context = failed;
            return failed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private ProcessingContext Transition(
        ProcessingContext context,
        ProcessingState state,
        string detail,
        string? lastAgentResponse = null,
        UserInteractionRequest? pendingUserInteraction = null,
        UserDecisionRequest? pendingDecisionRequest = null,
        UserAction? selectedAction = null,
        string? failureReason = null,
        string? additionalInstructions = null)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var timeline = context.Timeline.ToList();
        timeline.Add($"[{timestamp:O}] {state}: {detail}");

        var updated = context with
        {
            State = state,
            UpdatedUtc = timestamp,
            LastAgentResponse = lastAgentResponse ?? context.LastAgentResponse,
            PendingUserInteraction = pendingUserInteraction,
            PendingDecisionRequest = pendingDecisionRequest,
            SelectedAction = selectedAction ?? context.SelectedAction,
            FailureReason = failureReason,
            Timeline = timeline,
            AdditionalInstructions = additionalInstructions ?? context.AdditionalInstructions
        };

        return updated;
    }

    private static string BuildResumeMessage(
        ProcessingContext context,
        UserAction selectedAction,
        WorkflowDecision decision,
        string? additionalInstructions)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Resume the paused workflow.");
        builder.AppendLine($"WorkflowId: {context.WorkflowId:N}");
        builder.AppendLine($"Original user message: {context.InitialMessage}");
        if (!string.IsNullOrWhiteSpace(context.LastAgentResponse))
        {
            builder.AppendLine("Last agent response:");
            builder.AppendLine(context.LastAgentResponse);
        }

        builder.AppendLine("User decision:");
        builder.AppendLine($"Decision: {decision}");
        builder.AppendLine($"ActionId: {selectedAction.Id}");
        builder.AppendLine($"ActionTitle: {selectedAction.Title}");
        if (!string.IsNullOrWhiteSpace(selectedAction.Description))
        {
            builder.AppendLine($"ActionDescription: {selectedAction.Description}");
        }

        if (!string.IsNullOrWhiteSpace(additionalInstructions))
        {
            builder.AppendLine("Additional instructions:");
            builder.AppendLine(additionalInstructions.Trim());
        }

        builder.AppendLine("Continue from the current workflow state.");
        return builder.ToString();
    }

    private static UserDecisionRequest BuildDecisionRequest(Guid workflowId, UserInteractionRequest interaction)
    {
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
            OperationId = workflowId.ToString("N"),
            Title = "Workflow requires your decision",
            Message = interaction.Message,
            AvailableActions = actions
        };
    }

    private static WorkflowDecision MapDecision(UserAction action)
    {
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

    private static bool ContainsAny(string id, string title, params string[] tokens)
    {
        return tokens.Any(token =>
            id.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            title.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateRequest(ConversationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Message);
    }

    private ProcessingContext FailCurrentOrCreate(ConversationRequest request, string failureReason)
    {
        var now = DateTimeOffset.UtcNow;
        return _context is null
            ? new ProcessingContext(
                Guid.NewGuid(),
                ProcessingState.Failed,
                request,
                request.Message,
                null,
                null,
                null,
                null,
                failureReason,
                now,
                now,
                [$"[{now:O}] Failed: {failureReason}"],
                BuildOperationContext(request))
            : Transition(_context, ProcessingState.Failed, "Workflow failed.", failureReason: failureReason);
    }

    private static ProcessingContext CreateCancelled(ConversationRequest request, string detail)
    {
        var now = DateTimeOffset.UtcNow;
        return new ProcessingContext(
            Guid.NewGuid(),
            ProcessingState.Cancelled,
            request,
            request.Message,
            null,
            null,
            null,
            null,
            null,
            now,
            now,
            [$"[{now:O}] Cancelled: {detail}"],
            BuildOperationContext(request));
    }

    private static IReadOnlyDictionary<string, string?> BuildOperationContext(ConversationRequest request)
    {
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["provider"] = request.Provider.ToString(),
            ["url"] = request.Url,
            ["model"] = request.Model
        };
    }
}
