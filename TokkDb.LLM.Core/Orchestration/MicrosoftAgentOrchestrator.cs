using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using TokkDb.LLM.Core.Diagnostics;
using TokkDb.LLM.Core.Orchestration.Workflows;

namespace TokkDb.LLM.Core.Orchestration;

/// <summary>
/// Microsoft Agent Framework implementation of <see cref="IAgentOrchestrator"/>.
///
/// Chat runs as a real Agent Framework workflow with a human-in-the-loop
/// request port. Document processing is currently delegated to the existing
/// deterministic pipeline behind the same abstraction, so the UI contract does
/// not change when that pipeline is migrated to a workflow graph.
///
/// This class never touches storage: every state change reaches the domain
/// through controlled tools or domain services.
/// </summary>
public sealed class MicrosoftAgentOrchestrator : IAgentOrchestrator, IAsyncDisposable
{
    private readonly IConversationAgent _conversationAgent;
    private readonly IDocumentProcessingWorkflowService _documentProcessingWorkflowService;
    private readonly ILlmConfigurationProvider _configurationProvider;
    private readonly IWorkflowEventAdapter _eventAdapter;
    private readonly IDiagnosticsService _diagnostics;
    private readonly ILogger<MicrosoftAgentOrchestrator> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ChatWorkflowRun? _chatRun;
    private AgentOperationContext? _documentContext;
    private AgentOperationResult? _activeResult;

    public MicrosoftAgentOrchestrator(
        IConversationAgent conversationAgent,
        IDocumentProcessingWorkflowService documentProcessingWorkflowService,
        ILlmConfigurationProvider configurationProvider,
        IWorkflowEventAdapter eventAdapter,
        IDiagnosticsService diagnostics,
        ILogger<MicrosoftAgentOrchestrator> logger)
    {
        _conversationAgent = conversationAgent;
        _documentProcessingWorkflowService = documentProcessingWorkflowService;
        _configurationProvider = configurationProvider;
        _eventAdapter = eventAdapter;
        _diagnostics = diagnostics;
        _logger = logger;

        _conversationAgent.ToolExecutionStatusChanged += OnToolExecutionStatusChanged;
        _conversationAgent.ReasoningUpdated += OnReasoningUpdated;
        _conversationAgent.RecordsDisplayRequested += OnRecordsDisplayRequested;
    }

    public event EventHandler<AgentWorkflowEventArgs>? WorkflowEventRaised;

    public event EventHandler<AgentToolExecutionEventArgs>? ToolExecutionStatusChanged;

    public event EventHandler<AgentReasoningEventArgs>? ReasoningUpdated;

    public event EventHandler<RecordsDisplayEventArgs>? RecordsDisplayRequested;

    public AgentOperationResult? GetActiveOperation() => _activeResult;

    public async Task<AgentOperationResult> ExecuteAsync(
        AgentOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _logger.LogInformation(
                "Operation started. OperationType: {OperationType}, ConversationId: {ConversationId}, HasDocument: {HasDocument}",
                request.OperationType,
                request.ConversationId,
                request.DocumentPath is not null);

            var result = request.OperationType switch
            {
                AgentOperationType.DocumentAnalysis => await StartDocumentOperationAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                _ => await StartChatOperationAsync(request, cancellationToken).ConfigureAwait(false)
            };

            _logger.LogInformation(
                "Operation completed. OperationId: {OperationId}, OperationType: {OperationType}, WorkflowState: {WorkflowState}, Duration: {Duration}",
                result.Context.OperationId,
                result.Context.OperationType,
                result.State,
                stopwatch.Elapsed);

            return result;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(
                ex,
                "Operation cancelled. OperationType: {OperationType}, ConversationId: {ConversationId}, Duration: {Duration}",
                request.OperationType,
                request.ConversationId,
                stopwatch.Elapsed);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Operation failed. OperationType: {OperationType}, ConversationId: {ConversationId}, Duration: {Duration}",
                request.OperationType,
                request.ConversationId,
                stopwatch.Elapsed);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AgentOperationResult> ResumeAsync(
        AgentResumeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var operationId = request.OperationId.Trim();

            _logger.LogInformation(
                "Workflow resume requested. OperationId: {OperationId}, Decision: {Decision}, ActionId: {ActionId}, HasInstructions: {HasInstructions}",
                operationId,
                request.Decision,
                request.ActionId,
                !string.IsNullOrWhiteSpace(request.AdditionalInstructions));

            if (_chatRun is not null &&
                string.Equals(_chatRun.Context.OperationId, operationId, StringComparison.OrdinalIgnoreCase))
            {
                return await ResumeChatOperationAsync(_chatRun, request, cancellationToken).ConfigureAwait(false);
            }

            if (_documentContext is not null &&
                string.Equals(_documentContext.OperationId, operationId, StringComparison.OrdinalIgnoreCase))
            {
                return await ResumeDocumentOperationAsync(_documentContext, request, cancellationToken)
                    .ConfigureAwait(false);
            }

            throw new InvalidOperationException($"Operation '{operationId}' is not waiting for user input.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Reset()
    {
        var run = Interlocked.Exchange(ref _chatRun, null);
        if (run is not null)
        {
            // Fire-and-forget teardown: Reset is called from the UI thread.
            _ = run.DisposeAsync().AsTask();
        }

        _documentContext = null;
        _activeResult = null;
        _conversationAgent.ResetConversation();
    }

    public async ValueTask DisposeAsync()
    {
        _conversationAgent.ToolExecutionStatusChanged -= OnToolExecutionStatusChanged;
        _conversationAgent.ReasoningUpdated -= OnReasoningUpdated;
        _conversationAgent.RecordsDisplayRequested -= OnRecordsDisplayRequested;
        var run = Interlocked.Exchange(ref _chatRun, null);
        if (run is not null)
        {
            await run.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    // =====================================================================
    // Chat operation (Microsoft Agent Framework workflow)
    // =====================================================================

    private async Task<AgentOperationResult> StartChatOperationAsync(
        AgentOperationRequest request,
        CancellationToken cancellationToken)
    {
        var previous = Interlocked.Exchange(ref _chatRun, null);
        if (previous is not null)
        {
            await previous.DisposeAsync().ConfigureAwait(false);
        }

        var configuration = _configurationProvider.Resolve(request.OperationType);
        var context = AgentOperationContext.Create(request.OperationType, request.ConversationId, configuration);

        // Endpoint reference is provider+url only; the auth token is never logged.
        _logger.LogInformation(
            "LLM configuration selected. OperationId: {OperationId}, OperationType: {OperationType}, ConversationId: {ConversationId}, Provider: {Provider}, Model: {Model}, Endpoint: {Endpoint}",
            context.OperationId,
            context.OperationType,
            context.ConversationId,
            context.Provider,
            context.Model,
            context.EndpointReference);

        var run = new ChatWorkflowRun(context);
        _chatRun = run;

        RaiseWorkflowEvent(
            _eventAdapter.Create(AgentWorkflowEventKind.WorkflowStarted, context, "Workflow started."),
            run);

        try
        {
            var workflow = ChatWorkflowFactory.Build(
                _conversationAgent,
                configuration.ToConversationRequest(string.Empty, request.SystemPrompt));

            var handle = await InProcessExecution
                .RunStreamingAsync(workflow, new ChatAgentInput(request.Message))
                .ConfigureAwait(false);

            run.Attach(handle);
            run.Pump = Task.Run(() => PumpAsync(run), CancellationToken.None);
        }
        catch (Exception ex)
        {
            var failed = BuildResult(run, ProcessingState.Failed, null, null, ex.Message);
            _activeResult = failed;
            RaiseWorkflowEvent(
                _eventAdapter.Create(
                    AgentWorkflowEventKind.WorkflowFailed, context, "Workflow failed.", details: ex.Message),
                run);
            _logger.LogError(ex, "Chat workflow could not be started.");
            return failed;
        }

        return await AwaitTurnAsync(run, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentOperationResult> ResumeChatOperationAsync(
        ChatWorkflowRun run,
        AgentResumeRequest request,
        CancellationToken cancellationToken)
    {
        if (run.PendingRequest is null || run.PendingDecision is null)
        {
            throw new InvalidOperationException(
                $"Operation '{request.OperationId}' is not waiting for user input.");
        }

        var pendingDecision = run.PendingDecision;
        var selectedAction = WorkflowDecisionMapper.SelectAction(pendingDecision, request.Decision, request.ActionId);

        if (request.Decision == WorkflowDecision.ProvideInstructions &&
            string.IsNullOrWhiteSpace(request.AdditionalInstructions))
        {
            // Nothing to resume with: stay paused on the same decision.
            var stillWaiting = BuildResult(run, ProcessingState.WaitingForUser, null, pendingDecision);
            _activeResult = stillWaiting;
            return stillWaiting;
        }

        if (request.Decision == WorkflowDecision.Reject)
        {
            run.AddTimelineEntry(Stamp(ProcessingState.Cancelled, $"User rejected '{selectedAction?.Id ?? "operation"}'."));
            var cancelled = BuildResult(run, ProcessingState.Cancelled, null, null);
            _activeResult = cancelled;
            RaiseWorkflowEvent(
                _eventAdapter.Create(
                    AgentWorkflowEventKind.WorkflowCancelled, run.Context, "Operation cancelled by user."),
                run);

            var closing = Interlocked.Exchange(ref _chatRun, null);
            if (closing is not null)
            {
                await closing.DisposeAsync().ConfigureAwait(false);
            }

            return cancelled;
        }

        run.AddTimelineEntry(Stamp(ProcessingState.Resuming, $"Resuming with decision '{request.Decision}'."));
        RaiseWorkflowEvent(
            _eventAdapter.Create(AgentWorkflowEventKind.WorkflowResumed, run.Context, "Resuming operation."),
            run);

        var resumeInput = new ChatAgentInput(
            null,
            request.Decision,
            selectedAction?.Id,
            selectedAction?.Title,
            selectedAction?.Description,
            request.AdditionalInstructions);

        var externalRequest = run.PendingRequest;
        run.BeginTurn();
        run.CompleteResponse(externalRequest.CreateResponse(resumeInput));

        return await AwaitTurnAsync(run, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentOperationResult> AwaitTurnAsync(ChatWorkflowRun run, CancellationToken cancellationToken)
    {
        var turnTask = run.CurrentTurn;
        using var registration = cancellationToken.Register(run.RequestCancellation);
        return await turnTask.ConfigureAwait(false);
    }

    private async Task PumpAsync(ChatWorkflowRun run)
    {
        try
        {
            await foreach (var workflowEvent in run.Handle!
                               .WatchStreamAsync()
                               .WithCancellation(run.CancellationToken)
                               .ConfigureAwait(false))
            {
                switch (workflowEvent)
                {
                    case RequestInfoEvent requestInfo:
                    {
                        var decisionRequest = BuildDecisionRequest(run, requestInfo.Request);
                        run.PendingRequest = requestInfo.Request;
                        run.PendingDecision = decisionRequest;
                        run.AddTimelineEntry(Stamp(ProcessingState.WaitingForUser, decisionRequest.Message));

                        RaiseWorkflowEvent(
                            _eventAdapter.Adapt(workflowEvent, run.Context, decisionRequest), run);

                        // Arm the response slot BEFORE handing control back to the
                        // caller, otherwise a fast resume could complete a slot that
                        // is about to be replaced.
                        var responseTask = run.PrepareResponse();

                        var waiting = BuildResult(run, ProcessingState.WaitingForUser, null, decisionRequest);
                        _activeResult = waiting;
                        run.CompleteTurn(waiting);

                        var response = await responseTask.ConfigureAwait(false);
                        run.PendingRequest = null;
                        run.PendingDecision = null;
                        await run.Handle!.SendResponseAsync(response).ConfigureAwait(false);
                        break;
                    }

                    case WorkflowOutputEvent output:
                    {
                        RaiseWorkflowEvent(_eventAdapter.Adapt(workflowEvent, run.Context), run);
                        var turnOutput = output.Data as ChatTurnOutput;
                        var text = turnOutput?.Text ?? output.Data?.ToString();
                        run.AddTimelineEntry(Stamp(ProcessingState.Completed, "Workflow completed."));
                        var completed = BuildResult(
                            run, ProcessingState.Completed, text, null, usage: turnOutput?.Usage);
                        _activeResult = completed;
                        run.CompleteTurn(completed);
                        return;
                    }

                    case WorkflowErrorEvent error:
                    {
                        RaiseWorkflowEvent(_eventAdapter.Adapt(workflowEvent, run.Context), run);
                        FailRun(run, DescribeFailure(error.Exception, "Unknown workflow error."));
                        return;
                    }

                    case ExecutorFailedEvent failure:
                    {
                        RaiseWorkflowEvent(_eventAdapter.Adapt(workflowEvent, run.Context), run);

                        // The failure data is often an exception, whose ToString
                        // carries the whole stack trace. That belongs in the log,
                        // never in the chat.
                        _logger.LogError(
                            "Workflow step failed. ExecutorId: {ExecutorId}, OperationId: {OperationId}, Detail: {FailureDetail}",
                            failure.ExecutorId,
                            run.Context.OperationId,
                            failure.Data?.ToString());

                        FailRun(run, DescribeFailure(failure.Data, $"Step '{failure.ExecutorId}' failed."));
                        return;
                    }

                    default:
                        RaiseWorkflowEvent(_eventAdapter.Adapt(workflowEvent, run.Context), run);
                        break;
                }
            }

            run.AddTimelineEntry(Stamp(ProcessingState.Completed, "Workflow stream ended."));
            var settled = BuildResult(run, ProcessingState.Completed, null, null);
            _activeResult = settled;
            run.CompleteTurn(settled);
        }
        catch (OperationCanceledException)
        {
            run.AddTimelineEntry(Stamp(ProcessingState.Cancelled, "Workflow cancelled."));
            var cancelled = BuildResult(run, ProcessingState.Cancelled, null, null);
            _activeResult = cancelled;
            RaiseWorkflowEvent(
                _eventAdapter.Create(AgentWorkflowEventKind.WorkflowCancelled, run.Context, "Operation cancelled."),
                run);
            run.CompleteTurn(cancelled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat workflow failed for operation {OperationId}.", run.Context.OperationId);
            RaiseWorkflowEvent(
                _eventAdapter.Create(
                    AgentWorkflowEventKind.WorkflowFailed, run.Context, "Workflow failed.", details: ex.Message),
                run);
            FailRun(run, ex.Message);
        }
    }

    /// <summary>
    /// Short, user-safe description of a failure. Exceptions contribute their
    /// message only: a stack trace exposes internals and tells the user nothing
    /// they can act on.
    /// </summary>
    private static string DescribeFailure(object? data, string fallback)
    {
        var message = data switch
        {
            Exception exception => exception.InnerException?.Message ?? exception.Message,
            null => null,
            _ => data.ToString()
        };

        if (string.IsNullOrWhiteSpace(message))
        {
            return fallback;
        }

        // Anything multi-line is almost certainly a trace; keep the first line.
        var firstLine = message.Split('\n')[0].Trim();
        return firstLine.Length == 0 ? fallback : firstLine;
    }

    private void FailRun(ChatWorkflowRun run, string failureReason)
    {
        run.AddTimelineEntry(Stamp(ProcessingState.Failed, failureReason));
        var failed = BuildResult(run, ProcessingState.Failed, null, null, failureReason);
        _activeResult = failed;
        run.CompleteTurn(failed);
    }

    private static UserDecisionRequest BuildDecisionRequest(ChatWorkflowRun run, ExternalRequest externalRequest)
    {
        if (externalRequest.TryGetDataAs<ChatDecisionPrompt>(out var prompt) && prompt is not null)
        {
            return WorkflowDecisionMapper.ToDecisionRequest(
                run.Context.OperationId,
                new UserInteractionRequest(prompt.RequestId, prompt.Message, prompt.Actions, DateTimeOffset.UtcNow));
        }

        return new UserDecisionRequest
        {
            OperationId = run.Context.OperationId,
            Title = "Workflow requires your decision",
            Message = "The workflow is waiting for your input.",
            AvailableActions =
            [
                new WorkflowAction
                {
                    ActionId = "approve", Title = "Approve", Decision = WorkflowDecision.Approve
                },
                new WorkflowAction
                {
                    ActionId = "reject", Title = "Reject", Decision = WorkflowDecision.Reject
                }
            ]
        };
    }

    private AgentOperationResult BuildResult(
        ChatWorkflowRun run,
        ProcessingState state,
        string? text,
        UserDecisionRequest? pendingDecision,
        string? failureReason = null,
        AgentTokenUsage? usage = null)
    {
        return new AgentOperationResult(
            run.Context,
            state,
            text,
            pendingDecision,
            run.DrainToolExecutions(),
            run.SnapshotTimeline(),
            null,
            failureReason,
            usage);
    }

    // =====================================================================
    // Document operation (delegated to the deterministic pipeline)
    // =====================================================================

    private async Task<AgentOperationResult> StartDocumentOperationAsync(
        AgentOperationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DocumentPath);

        var configuration = _configurationProvider.Resolve(AgentOperationType.DocumentAnalysis);
        var seedContext = AgentOperationContext.Create(
            AgentOperationType.DocumentAnalysis, request.ConversationId, configuration);

        _logger.LogInformation(
            "LLM configuration selected. OperationId: {OperationId}, OperationType: {OperationType}, ConversationId: {ConversationId}, Provider: {Provider}, Model: {Model}, Endpoint: {Endpoint}",
            seedContext.OperationId,
            seedContext.OperationType,
            seedContext.ConversationId,
            seedContext.Provider,
            seedContext.Model,
            seedContext.EndpointReference);

        RaiseWorkflowEvent(
            _eventAdapter.Create(
                AgentWorkflowEventKind.WorkflowStarted, seedContext, "Document processing started."),
            null);

        try
        {
            var documentContext = await _documentProcessingWorkflowService
                .StartAsync(
                    request.DocumentPath,
                    configuration.ToConversationRequest(request.Message, request.SystemPrompt),
                    cancellationToken)
                .ConfigureAwait(false);

            var context = seedContext with { OperationId = documentContext.OperationId };
            _documentContext = context;
            return PublishDocumentResult(context, documentContext);
        }
        catch (OperationCanceledException)
        {
            return PublishCancelled(seedContext, "Document processing cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Document processing failed to start.");
            return PublishFailed(seedContext, ex.Message);
        }
    }

    private async Task<AgentOperationResult> ResumeDocumentOperationAsync(
        AgentOperationContext context,
        AgentResumeRequest request,
        CancellationToken cancellationToken)
    {
        RaiseWorkflowEvent(
            _eventAdapter.Create(
                AgentWorkflowEventKind.WorkflowResumed, context, "Resuming document processing."),
            null);

        try
        {
            var documentContext = await _documentProcessingWorkflowService
                .ResumeAsync(context.OperationId, request.Decision, request.AdditionalInstructions, cancellationToken)
                .ConfigureAwait(false);

            return PublishDocumentResult(context, documentContext);
        }
        catch (OperationCanceledException)
        {
            return PublishCancelled(context, "Document processing cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Document processing failed to resume.");
            return PublishFailed(context, ex.Message);
        }
    }

    private AgentOperationResult PublishDocumentResult(
        AgentOperationContext context,
        DocumentProcessingContext documentContext)
    {
        var kind = documentContext.State switch
        {
            ProcessingState.WaitingForUser => AgentWorkflowEventKind.WorkflowWaitingForUser,
            ProcessingState.Completed => AgentWorkflowEventKind.WorkflowCompleted,
            ProcessingState.Cancelled => AgentWorkflowEventKind.WorkflowCancelled,
            ProcessingState.Failed => AgentWorkflowEventKind.WorkflowFailed,
            _ => AgentWorkflowEventKind.WorkflowProgress
        };

        RaiseWorkflowEvent(
            _eventAdapter.Create(
                kind,
                context,
                documentContext.StatusMessage,
                documentContext.PendingDecisionRequest,
                documentContext.FailureReason),
            null);

        var result = new AgentOperationResult(
            context,
            documentContext.State,
            DocumentOutcomeFormatter.Build(documentContext),
            documentContext.PendingDecisionRequest,
            Array.Empty<AgentToolExecution>(),
            documentContext.Timeline,
            documentContext.StatusMessage,
            documentContext.FailureReason);

        _activeResult = result;
        return result;
    }

    private AgentOperationResult PublishCancelled(AgentOperationContext context, string message)
    {
        RaiseWorkflowEvent(
            _eventAdapter.Create(AgentWorkflowEventKind.WorkflowCancelled, context, message), null);
        var result = new AgentOperationResult(
            context,
            ProcessingState.Cancelled,
            message,
            null,
            Array.Empty<AgentToolExecution>(),
            [Stamp(ProcessingState.Cancelled, message)],
            message);
        _activeResult = result;
        return result;
    }

    private AgentOperationResult PublishFailed(AgentOperationContext context, string failureReason)
    {
        RaiseWorkflowEvent(
            _eventAdapter.Create(
                AgentWorkflowEventKind.WorkflowFailed, context, "Operation failed.", details: failureReason),
            null);
        var result = new AgentOperationResult(
            context,
            ProcessingState.Failed,
            null,
            null,
            Array.Empty<AgentToolExecution>(),
            [Stamp(ProcessingState.Failed, failureReason)],
            null,
            failureReason);
        _activeResult = result;
        return result;
    }

    // =====================================================================
    // Diagnostics and events
    // =====================================================================

    private void RaiseWorkflowEvent(AgentWorkflowEvent? workflowEvent, ChatWorkflowRun? run = null)
    {
        if (workflowEvent is null)
        {
            return;
        }

        if (run is not null && workflowEvent.Kind != AgentWorkflowEventKind.WorkflowProgress)
        {
            run.AddTimelineEntry($"[{workflowEvent.TimestampUtc:O}] {workflowEvent.Kind}: {workflowEvent.Message}");
        }

        // Every workflow state change is logged with the full operation context.
        if (workflowEvent.Kind == AgentWorkflowEventKind.WorkflowFailed)
        {
            _logger.LogError(
                "Workflow state changed. WorkflowState: {WorkflowState}, OperationId: {OperationId}, OperationType: {OperationType}, Message: {Message}, Details: {Details}",
                workflowEvent.Kind,
                workflowEvent.OperationId,
                workflowEvent.OperationType,
                workflowEvent.Message,
                workflowEvent.Details);
        }
        else if (workflowEvent.Kind == AgentWorkflowEventKind.WorkflowProgress)
        {
            _logger.LogDebug(
                "Workflow progress. WorkflowState: {WorkflowState}, OperationId: {OperationId}, OperationType: {OperationType}, Message: {Message}",
                workflowEvent.Kind,
                workflowEvent.OperationId,
                workflowEvent.OperationType,
                workflowEvent.Message);
        }
        else
        {
            _logger.LogInformation(
                "Workflow state changed. WorkflowState: {WorkflowState}, OperationId: {OperationId}, OperationType: {OperationType}, Message: {Message}",
                workflowEvent.Kind,
                workflowEvent.OperationId,
                workflowEvent.OperationType,
                workflowEvent.Message);
        }

        _diagnostics.Log(new DiagnosticEvent(
            workflowEvent.TimestampUtc,
            workflowEvent.Kind is AgentWorkflowEventKind.WorkflowFailed
                ? DiagnosticLevel.Error
                : DiagnosticLevel.Information,
            nameof(MicrosoftAgentOrchestrator),
            workflowEvent.OperationType.ToString(),
            workflowEvent.Kind.ToString(),
            workflowEvent.Message,
            workflowEvent.Details));

        WorkflowEventRaised?.Invoke(this, new AgentWorkflowEventArgs(workflowEvent));
    }

    private void OnRecordsDisplayRequested(object? sender, RecordsDisplayEventArgs e)
    {
        // Record values are not logged: only the shape of the request.
        _logger.LogInformation(
            "Records display requested. Collection: {CollectionName}, Requested: {RequestedRecordCount}, Resolved: {ResolvedCount}, OperationId: {OperationId}",
            e.Message.CollectionName,
            e.Message.RequestedRecordCount,
            e.Message.Records.Count,
            _chatRun?.Context.OperationId);

        RecordsDisplayRequested?.Invoke(this, e);
    }

    private void OnReasoningUpdated(object? sender, AgentReasoningEventArgs e)
    {
        // Reasoning text itself is not written to diagnostics: it can be long and
        // is already visible in the chat. Only the shape of the stream is logged.
        if (e.Update.IsCompleted)
        {
            // Reasoning text itself is never logged - only that it arrived.
            _logger.LogInformation(
                "Reasoning segment received. SegmentId: {SegmentId}, OperationId: {OperationId}",
                e.Update.SegmentId,
                _chatRun?.Context.OperationId);

            _diagnostics.Log(new DiagnosticEvent(
                e.Update.TimestampUtc,
                DiagnosticLevel.Information,
                nameof(MicrosoftAgentOrchestrator),
                "Reasoning",
                "SegmentCompleted",
                $"Reasoning segment {e.Update.SegmentId} completed."));
        }

        ReasoningUpdated?.Invoke(this, e);
    }

    private void OnToolExecutionStatusChanged(object? sender, AgentToolExecutionEventArgs e)
    {
        _chatRun?.RecordToolExecution(e.Execution);

        var context = _chatRun?.Context;
        if (e.Execution.Status == AgentToolExecutionStatus.Failed)
        {
            _logger.LogError(
                "Tool execution failed. ToolName: {ToolName}, CallId: {CallId}, OperationId: {OperationId}, ConversationId: {ConversationId}, Details: {Details}",
                e.Execution.Name,
                e.Execution.CallId,
                context?.OperationId,
                context?.ConversationId,
                e.Execution.Details);
        }
        else
        {
            _logger.LogInformation(
                "Tool call {ToolStatus}. ToolName: {ToolName}, CallId: {CallId}, OperationId: {OperationId}, ConversationId: {ConversationId}",
                e.Execution.Status,
                e.Execution.Name,
                e.Execution.CallId,
                context?.OperationId,
                context?.ConversationId);
        }

        _diagnostics.Log(new DiagnosticEvent(
            e.Execution.TimestampUtc,
            e.Execution.Status == AgentToolExecutionStatus.Failed
                ? DiagnosticLevel.Error
                : DiagnosticLevel.Information,
            nameof(MicrosoftAgentOrchestrator),
            "Tool",
            e.Execution.Name,
            e.Execution.Status.ToString(),
            e.Execution.Details));

        ToolExecutionStatusChanged?.Invoke(this, e);
    }

    private static string Stamp(ProcessingState state, string detail)
    {
        return $"[{DateTimeOffset.UtcNow:O}] {state}: {detail}";
    }

    // =====================================================================
    // Run state
    // =====================================================================

    private sealed class ChatWorkflowRun : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly List<AgentToolExecution> _toolExecutions = new();
        private readonly object _sync = new();

        private TaskCompletionSource<AgentOperationResult> _turn =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource<ExternalResponse> _response =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ChatWorkflowRun(AgentOperationContext context)
        {
            Context = context;
            _timeline = [Stamp(ProcessingState.Running, "Workflow started.")];
        }

        public AgentOperationContext Context { get; }

        private readonly List<string> _timeline;

        public StreamingRun? Handle { get; private set; }

        public Task? Pump { get; set; }

        public ExternalRequest? PendingRequest { get; set; }

        public UserDecisionRequest? PendingDecision { get; set; }

        public CancellationToken CancellationToken => _cancellation.Token;

        public Task<AgentOperationResult> CurrentTurn
        {
            get
            {
                lock (_sync)
                {
                    return _turn.Task;
                }
            }
        }

        public void Attach(StreamingRun handle) => Handle = handle;

        public void BeginTurn()
        {
            lock (_sync)
            {
                _turn = new TaskCompletionSource<AgentOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void CompleteTurn(AgentOperationResult result)
        {
            lock (_sync)
            {
                _turn.TrySetResult(result);
            }
        }

        /// <summary>
        /// Arms a fresh response slot and returns the task that completes when
        /// the application supplies the user's answer.
        /// </summary>
        public Task<ExternalResponse> PrepareResponse()
        {
            lock (_sync)
            {
                _response = new TaskCompletionSource<ExternalResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _response.Task;
            }
        }

        public void CompleteResponse(ExternalResponse response)
        {
            lock (_sync)
            {
                _response.TrySetResult(response);
            }
        }

        public void AddTimelineEntry(string entry)
        {
            lock (_sync)
            {
                _timeline.Add(entry);
            }
        }

        public IReadOnlyCollection<string> SnapshotTimeline()
        {
            lock (_sync)
            {
                return _timeline.ToArray();
            }
        }

        public void RecordToolExecution(AgentToolExecution execution)
        {
            lock (_sync)
            {
                _toolExecutions.Add(execution);
            }
        }

        public IReadOnlyCollection<AgentToolExecution> DrainToolExecutions()
        {
            lock (_sync)
            {
                var snapshot = _toolExecutions.ToArray();
                _toolExecutions.Clear();
                return snapshot;
            }
        }

        public void RequestCancellation()
        {
            if (!_cancellation.IsCancellationRequested)
            {
                _cancellation.Cancel();
            }

            lock (_sync)
            {
                _response.TrySetCanceled();
            }
        }

        public async ValueTask DisposeAsync()
        {
            RequestCancellation();

            if (Pump is not null)
            {
                try
                {
                    await Pump.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected on cancellation.
                }
                catch (Exception)
                {
                    // The pump already reported the failure.
                }
            }

            if (Handle is not null)
            {
                await Handle.DisposeAsync().ConfigureAwait(false);
            }

            _cancellation.Dispose();
        }
    }
}
