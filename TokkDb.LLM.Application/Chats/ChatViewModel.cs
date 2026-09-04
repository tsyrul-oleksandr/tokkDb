using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using TokkDb.LLM.Core;
using TokkDb.LLM.Core.Orchestration;

namespace TokkDb.LLM.Application.Chats;

/// <summary>
/// Chat surface. Depends only on <see cref="IAgentOrchestrator"/> and
/// application-level workflow contracts; it never sees Microsoft Agent
/// Framework types.
/// </summary>
public sealed class ChatViewModel : INotifyPropertyChanged
{
    private readonly IAgentOrchestrator _orchestrator;
    private readonly IRecordNavigationService _recordNavigation;
    private readonly IConversationHistoryService _history;
    private readonly ILogger<ChatViewModel> _logger;
    private readonly Dictionary<string, ReasoningModel> _reasoningSegments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolExecutionModel> _toolCalls = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cancellationTokenSource;

    private string _conversationId = Guid.NewGuid().ToString("N");
    private string _prompt = string.Empty;
    private string _responseStatus = "Ready";
    private AgentOperationResult? _activeOperation;
    private bool _isBusy;
    private bool _renderedAnswerDuringOperation;
    private ChatConversation? _selectedConversation;
    private bool _suppressConversationSwitch;
    private string? _lastRenderedDecisionRequestId;

    public ChatViewModel(
        IAgentOrchestrator orchestrator,
        IRecordNavigationService recordNavigation,
        IConversationHistoryService history,
        ILogger<ChatViewModel> logger)
    {
        _orchestrator = orchestrator;
        _recordNavigation = recordNavigation;
        _history = history;
        _logger = logger;

        SendCommand = new Command(async () => await SendAsync(), () => CanSend);
        UploadDocumentCommand = new Command(async () => await UploadDocumentAsync(), () => !IsBusy);
        NewChatCommand = new Command(NewChat);
        DeleteConversationCommand = new Command<ChatConversation>(DeleteConversation);
        CopyConversationCommand = new Command(
            async () => await CopyConversationAsync(),
            () => Messages.Count > 0);
        CancelCommand = new Command(Cancel, () => IsBusy);
        WorkflowActionCommand = new Command<WorkflowActionModel>(
            async model => await ResumeByWorkflowActionAsync(model), _ => !IsBusy);

        // Keep the copy action enabled in step with the transcript.
        Messages.CollectionChanged += (_, _) =>
            ((Command)CopyConversationCommand).ChangeCanExecute();

        _orchestrator.ToolExecutionStatusChanged += OnToolExecutionStatusChanged;
        _orchestrator.WorkflowEventRaised += OnWorkflowEventRaised;
        _orchestrator.ReasoningUpdated += OnReasoningUpdated;
        _orchestrator.RecordsDisplayRequested += OnRecordsDisplayRequested;

        StartOrResumeInitialConversation();

        var persisted = _orchestrator.GetActiveOperation();
        if (persisted is not null)
        {
            ApplyOperationResult(persisted);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ChatMessage> Messages { get; } = [];

    public ObservableCollection<ChatConversation> Conversations { get; } = [];

    /// <summary>
    /// Bound to the sidebar. Assigning a different conversation switches to it
    /// and restores its history.
    /// </summary>
    public ChatConversation? SelectedConversation
    {
        get => _selectedConversation;
        set
        {
            if (ReferenceEquals(_selectedConversation, value))
            {
                return;
            }

            _selectedConversation = value;
            OnPropertyChanged();

            // Suppressed while the list is being rebuilt, so re-selecting the
            // current conversation does not reload it.
            if (!_suppressConversationSwitch && value is not null && value.Id != _conversationId)
            {
                SwitchConversation(value.Id);
            }
        }
    }

    public ICommand SendCommand { get; }

    public ICommand UploadDocumentCommand { get; }

    public ICommand NewChatCommand { get; }

    public ICommand DeleteConversationCommand { get; }

    public ICommand CopyConversationCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand WorkflowActionCommand { get; }

    public string SelectedProvider => Settings.Settings.Instance.Provider.ToString();

    public string Model => Settings.Settings.Instance.ProviderModel;

    public string Prompt
    {
        get => _prompt;
        set
        {
            if (_prompt == value)
            {
                return;
            }

            _prompt = value;
            OnPropertyChanged();
            ((Command)SendCommand).ChangeCanExecute();
        }
    }

    public string ResponseStatus
    {
        get => _responseStatus;
        private set
        {
            if (_responseStatus == value)
            {
                return;
            }

            _responseStatus = value;
            OnPropertyChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value)
            {
                return;
            }

            _isBusy = value;
            OnPropertyChanged();
            ((Command)SendCommand).ChangeCanExecute();
            ((Command)UploadDocumentCommand).ChangeCanExecute();
            ((Command)CancelCommand).ChangeCanExecute();
            ((Command<WorkflowActionModel>)WorkflowActionCommand).ChangeCanExecute();
        }
    }

    public bool HasMessages => Messages.Count > 0;

    public string ConversationTitle =>
        Messages.FirstOrDefault(message => message.IsUser)?.Content ?? "New Chat";

    public bool CanSend => !IsBusy && !string.IsNullOrWhiteSpace(Prompt);

    // =====================================================================
    // Conversation history
    // =====================================================================

    private void StartOrResumeInitialConversation()
    {
        var existing = _history.GetConversations();
        var conversation = existing.Count > 0 ? existing[0] : _history.Create();

        _conversationId = conversation.Id;
        RefreshConversations();
        RestoreMessages(conversation);
    }

    /// <summary>
    /// Rebuilds the sidebar from history, keeping the current conversation
    /// selected without triggering another switch.
    /// </summary>
    private void RefreshConversations()
    {
        _suppressConversationSwitch = true;
        try
        {
            Conversations.Clear();
            foreach (var conversation in _history.GetConversations())
            {
                Conversations.Add(new ChatConversation
                {
                    Id = conversation.Id,
                    Title = conversation.Title,
                    LastUpdated = conversation.UpdatedAt.LocalDateTime,
                    MessageCount = conversation.MessageCount
                });
            }

            _selectedConversation = Conversations.FirstOrDefault(item => item.Id == _conversationId);
            OnPropertyChanged(nameof(SelectedConversation));
        }
        finally
        {
            _suppressConversationSwitch = false;
        }
    }

    private void SwitchConversation(string conversationId)
    {
        var conversation = _history.GetConversation(conversationId);
        if (conversation is null)
        {
            _logger.LogWarning(
                "Conversation switch ignored, not found. ConversationId: {ConversationId}",
                conversationId);
            return;
        }

        _logger.LogInformation(
            "Conversation switched. FromConversationId: {PreviousConversationId}, ToConversationId: {ConversationId}, Messages: {MessageCount}",
            _conversationId,
            conversation.Id,
            conversation.MessageCount);

        Cancel();

        // The agent session belongs to the previous conversation.
        _orchestrator.Reset();

        _conversationId = conversation.Id;
        RestoreMessages(conversation);
        RefreshConversations();
    }

    /// <summary>
    /// Replaces the visible transcript with the stored conversation, in order.
    /// </summary>
    private void RestoreMessages(StoredConversation conversation)
    {
        Messages.Clear();
        _reasoningSegments.Clear();
        _toolCalls.Clear();
        _activeOperation = null;
        _lastRenderedDecisionRequestId = null;

        foreach (var entry in conversation.Entries)
        {
            var message = ChatHistoryMapper.ToMessage(entry, OpenRecord);
            if (message is null)
            {
                continue;
            }

            Messages.Add(message);

            // Keep the live-update lookups consistent with what is on screen.
            if (message.ToolExecution is not null && !string.IsNullOrEmpty(message.ToolExecution.CallId))
            {
                _toolCalls[message.ToolExecution.CallId] = message.ToolExecution;
            }

            if (message.Reasoning is not null && !string.IsNullOrEmpty(message.Reasoning.SegmentId))
            {
                _reasoningSegments[message.Reasoning.SegmentId] = message.Reasoning;
            }
        }

        ResponseStatus = "Ready";
        Prompt = string.Empty;
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(ConversationTitle));
    }

    /// <summary>
    /// Stores a rendered message. Called for new messages and for in-place
    /// updates, which the history keys by the event's own id.
    /// </summary>
    private void RecordToolHistory(ToolExecutionModel tool)
    {
        var message = Messages.FirstOrDefault(candidate => ReferenceEquals(candidate.ToolExecution, tool));
        if (message is not null)
        {
            RecordHistory(message);
        }
    }

    private void RecordReasoningHistory(ReasoningModel reasoning)
    {
        var message = Messages.FirstOrDefault(candidate => ReferenceEquals(candidate.Reasoning, reasoning));
        if (message is not null)
        {
            RecordHistory(message);
        }
    }

    private void RecordHistory(ChatMessage message, bool refreshList = false)
    {
        var entry = ChatHistoryMapper.ToEntry(message);
        if (entry is null)
        {
            return;
        }

        _history.Append(_conversationId, entry);

        if (refreshList)
        {
            RefreshConversations();
        }
    }

    private void DeleteConversation(ChatConversation? conversation)
    {
        if (conversation is null)
        {
            return;
        }

        if (!_history.Delete(conversation.Id))
        {
            return;
        }

        if (conversation.Id != _conversationId)
        {
            RefreshConversations();
            return;
        }

        // The active conversation went away: fall back to the most recent one,
        // or start a fresh conversation when none is left.
        Cancel();
        _orchestrator.Reset();

        var remaining = _history.GetConversations();
        var next = remaining.Count > 0 ? remaining[0] : _history.Create();

        _conversationId = next.Id;
        RestoreMessages(next);
        RefreshConversations();
    }

    // =====================================================================
    // Message routing
    // =====================================================================

    private async Task SendAsync()
    {
        if (!CanSend)
        {
            return;
        }

        var prompt = Prompt.Trim();
        Prompt = string.Empty;
        AddUserMessage(prompt);

        await RunAsync(async token =>
        {
            var waiting = _activeOperation;
            if (waiting is not null && waiting.IsWaitingForUser)
            {
                // A workflow is paused: route the free-form message into it
                // instead of starting a new operation.
                ResponseStatus = "Resuming...";
                return await _orchestrator.ResumeAsync(
                    new AgentResumeRequest(
                        waiting.Context.OperationId,
                        WorkflowDecision.ProvideInstructions,
                        AdditionalInstructions: prompt),
                    token);
            }

            ResponseStatus = "Thinking...";
            return await _orchestrator.ExecuteAsync(
                new AgentOperationRequest(AgentOperationType.Chat, _conversationId, prompt),
                token);
        });
    }

    private async Task ResumeByWorkflowActionAsync(WorkflowActionModel? workflowAction)
    {
        if (workflowAction is null || IsBusy)
        {
            return;
        }

        AddUserMessage(workflowAction.Title);

        await RunAsync(async token =>
        {
            ResponseStatus = "Resuming...";
            return await _orchestrator.ResumeAsync(
                new AgentResumeRequest(
                    workflowAction.WorkflowOperationId,
                    workflowAction.Decision,
                    workflowAction.ActionId),
                token);
        });
    }

    /// <summary>
    /// Copies the whole conversation to the clipboard as JSON.
    ///
    /// The export is built from the structured data behind each message, not
    /// from the rendered text, and confirmation reuses the existing status line
    /// rather than interrupting with a dialog.
    /// </summary>
    private async Task CopyConversationAsync()
    {
        if (Messages.Count == 0)
        {
            return;
        }

        try
        {
            var export = ChatConversationExporter.Build(Messages);
            var json = ConversationJsonSerializer.Serialize(export);

            await Clipboard.Default.SetTextAsync(json);

            _logger.LogInformation(
                "Conversation copied to clipboard. ConversationId: {ConversationId}, Messages: {MessageCount}, Length: {Length}",
                _conversationId,
                export.Messages.Count,
                json.Length);

            ResponseStatus = "Conversation copied";
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to copy the conversation to the clipboard. ConversationId: {ConversationId}",
                _conversationId);

            ResponseStatus = "Could not copy conversation";
        }
    }

    private async Task UploadDocumentAsync()
    {
        if (IsBusy)
        {
            return;
        }

        FileResult? picked;
        try
        {
            picked = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Choose CSV or XLSX file"
            });
        }
        catch (Exception ex)
        {
            AddSystemMessage(ex.Message);
            ResponseStatus = "Error";
            return;
        }

        if (picked is null)
        {
            return;
        }

        var fileName = picked.FileName;
        var fullPath = picked.FullPath;
        AddUserMessage($"Upload document: {fileName}");

        await RunAsync(async token =>
        {
            ResponseStatus = "Analyzing document...";
            return await _orchestrator.ExecuteAsync(
                new AgentOperationRequest(
                    AgentOperationType.DocumentAnalysis,
                    _conversationId,
                    $"Analyze and import document '{fileName}'.",
                    fullPath),
                token);
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task<AgentOperationResult>> operation)
    {
        try
        {
            Cancel();
            _cancellationTokenSource = new CancellationTokenSource();
            _renderedAnswerDuringOperation = false;
            IsBusy = true;

            var result = await operation(_cancellationTokenSource.Token);
            ApplyOperationResult(result);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation(
                ex,
                "Chat operation cancelled by user. ConversationId: {ConversationId}",
                _conversationId);
            ResponseStatus = "Canceled";
        }
        catch (LlmProviderException ex)
        {
            _logger.LogError(
                ex,
                "LLM provider request failed. ConversationId: {ConversationId}, StatusCode: {StatusCode}",
                _conversationId,
                ex.StatusCode);
            ResponseStatus = ex.Message;
            if (!string.IsNullOrWhiteSpace(ex.ResponseBody))
            {
                AddAssistantMessage(ex.ResponseBody);
            }
        }
        catch (Exception ex)
        {
            // Handled so the chat stays usable, but never swallowed.
            _logger.LogError(
                ex,
                "Chat operation failed. ConversationId: {ConversationId}",
                _conversationId);
            ResponseStatus = "Error";
            AddSystemMessage(ex.Message);
        }
        finally
        {
            IsBusy = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    // =====================================================================
    // Rendering
    // =====================================================================

    private void ApplyOperationResult(AgentOperationResult result)
    {
        _activeOperation = result;

        if (!string.IsNullOrWhiteSpace(result.Text))
        {
            _renderedAnswerDuringOperation = true;
            AddAssistantMessage(result.Text, ToUsageModel(result.Usage));
        }

        if (result.IsWaitingForUser && result.PendingDecision is not null)
        {
            _renderedAnswerDuringOperation = true;
            AddWorkflowDecisionMessage(result.PendingDecision, result.State);
        }

        if (result.State == ProcessingState.Failed && !string.IsNullOrWhiteSpace(result.FailureReason))
        {
            _renderedAnswerDuringOperation = true;
            AddSystemMessage(result.FailureReason);
        }

        // A turn can finish having produced only reasoning and tool calls - some
        // models end without any visible reply. Say so, otherwise the chat looks
        // like it stopped responding.
        if (result.State == ProcessingState.Completed && !_renderedAnswerDuringOperation)
        {
            _logger.LogWarning(
                "Operation completed without any visible reply. OperationId: {OperationId}, ConversationId: {ConversationId}, ToolCalls: {ToolCallCount}",
                result.Context.OperationId,
                _conversationId,
                result.ToolExecutions.Count);

            AddSystemMessage(
                result.ToolExecutions.Count > 0
                    ? "The model finished this turn without a reply, although the tool calls above did run. "
                      + "Try asking again, or rephrase the request."
                    : "The model finished this turn without a reply. Try asking again, or rephrase the request.");
        }

        ResponseStatus = BuildStatus(result);
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(ConversationTitle));
    }

    private void AddWorkflowDecisionMessage(UserDecisionRequest request, ProcessingState state)
    {
        if (string.Equals(_lastRenderedDecisionRequestId, request.RequestId, StringComparison.Ordinal))
        {
            return;
        }

        var decisionMessage = new ChatMessage
        {
            Role = ChatMessageRole.Workflow,
            Kind = ChatMessageKind.Workflow,
            Content = BuildWorkflowDecisionMessage(request),
            Timestamp = DateTime.Now,
            Workflow = new WorkflowModel
            {
                WorkflowOperationId = request.OperationId,
                WorkflowStatus = state.ToString(),
                DecisionRequest = request,
                Message = request.Message,
                AvailableActions = request.AvailableActions
                    .Select(action => new WorkflowActionModel
                    {
                        WorkflowOperationId = request.OperationId,
                        Action = action
                    })
                    .ToArray()
            }
        };
        Messages.Add(decisionMessage);
        RecordHistory(decisionMessage);
        _lastRenderedDecisionRequestId = request.RequestId;
        OnPropertyChanged(nameof(HasMessages));
    }

    private void OnWorkflowEventRaised(object? sender, AgentWorkflowEventArgs e)
    {
        var workflowEvent = e.WorkflowEvent;
        var text = BuildWorkflowStateText(workflowEvent);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            var workflowMessage = new ChatMessage
            {
                Role = ChatMessageRole.Workflow,
                Kind = ChatMessageKind.Workflow,
                Content = text,
                Timestamp = DateTime.Now,
                Workflow = new WorkflowModel
                {
                    WorkflowOperationId = workflowEvent.OperationId,
                    WorkflowStatus = workflowEvent.Kind.ToString(),
                    Message = text
                }
            };
            Messages.Add(workflowMessage);
            RecordHistory(workflowMessage);
            OnPropertyChanged(nameof(HasMessages));
        });
    }

    /// <summary>
    /// Renders a record list as its own chat message. The records never appear
    /// as assistant text: this is built from the structured display model.
    /// </summary>
    private void OnRecordsDisplayRequested(object? sender, RecordsDisplayEventArgs e)
    {
        var message = e.Message;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            var items = message.Records
                .Select(record => new RecordDisplayItemModel
                {
                    RecordId = record.RecordId,
                    CollectionName = record.CollectionName,
                    DisplayValue = record.DisplayValue,
                    AdditionalFields = record.AdditionalFields
                        .Select(field => new RecordFieldModel { Name = field.Name, Value = field.Value })
                        .ToArray(),
                    OpenCommand = new Command(() => OpenRecord(record.CollectionName, record.RecordId))
                })
                .ToArray();

            _renderedAnswerDuringOperation = true;

            var recordsMessage = new ChatMessage
            {
                Role = ChatMessageRole.Assistant,
                Kind = ChatMessageKind.Records,
                Content = string.Empty,
                Timestamp = DateTime.Now,
                Records = new RecordsDisplayModel
                {
                    CollectionName = message.CollectionName,
                    Records = items,
                    RequestedAdditionalFields = message.RequestedAdditionalFields,
                    Notice = BuildRecordsNotice(message)
                }
            };
            Messages.Add(recordsMessage);
            RecordHistory(recordsMessage);

            OnPropertyChanged(nameof(HasMessages));
        });
    }

    private static string? BuildRecordsNotice(RecordsDisplayMessage message)
    {
        var parts = new List<string>();
        if (message.UnresolvedRecordIds.Count > 0)
        {
            parts.Add($"{message.UnresolvedRecordIds.Count} record(s) could not be found");
        }

        if (message.InvalidAdditionalFields.Count > 0)
        {
            parts.Add($"unknown field(s): {string.Join(", ", message.InvalidAdditionalFields)}");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts) + ".";
    }

    private void OpenRecord(string collectionName, string recordId)
    {
        _logger.LogInformation(
            "Record opened from chat. CollectionName: {CollectionName}, RecordId: {RecordId}",
            collectionName,
            recordId);

        _recordNavigation.OpenRecord(new OpenRecordRequest(collectionName, recordId));
    }

    /// <summary>
    /// Renders streamed reasoning. Each segment becomes one collapsible message,
    /// appended as soon as its first delta arrives, so reasoning is shown ahead
    /// of the assistant answer for the turn it belongs to.
    /// </summary>
    private void OnReasoningUpdated(object? sender, AgentReasoningEventArgs e)
    {
        var update = e.Update;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_reasoningSegments.TryGetValue(update.SegmentId, out var existing))
            {
                existing.Append(update.Delta);
                if (update.IsCompleted)
                {
                    existing.IsStreaming = false;
                    // Store the finished segment rather than every delta.
                    RecordReasoningHistory(existing);
                }

                return;
            }

            if (update.IsCompleted || string.IsNullOrEmpty(update.Delta))
            {
                // Nothing was streamed for this segment: nothing to show.
                return;
            }

            var reasoning = new ReasoningModel { SegmentId = update.SegmentId };
            reasoning.Append(update.Delta);
            _reasoningSegments[update.SegmentId] = reasoning;

            var reasoningMessage = new ChatMessage
            {
                Role = ChatMessageRole.Assistant,
                Kind = ChatMessageKind.Reasoning,
                Content = string.Empty,
                Timestamp = DateTime.Now,
                Reasoning = reasoning
            };
            Messages.Add(reasoningMessage);
            RecordHistory(reasoningMessage);

            OnPropertyChanged(nameof(HasMessages));
        });
    }

    /// <summary>
    /// Renders tool calls. The first transition of a call appends a message
    /// immediately, so the UI reacts as soon as the tool starts; later
    /// transitions of the same call update that message in place, which is what
    /// keeps one entry per call and preserves chronological order.
    /// </summary>
    private void OnToolExecutionStatusChanged(object? sender, AgentToolExecutionEventArgs e)
    {
        var execution = e.Execution;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!string.IsNullOrEmpty(execution.CallId) &&
                _toolCalls.TryGetValue(execution.CallId, out var existing))
            {
                existing.Apply(execution);
                RecordToolHistory(existing);
                return;
            }

            var model = new ToolExecutionModel
            {
                CallId = execution.CallId,
                Name = execution.Name,
                TimestampUtc = execution.TimestampUtc
            };
            model.Apply(execution);

            if (!string.IsNullOrEmpty(execution.CallId))
            {
                _toolCalls[execution.CallId] = model;
            }

            var message = new ChatMessage
            {
                Role = ChatMessageRole.Assistant,
                Kind = ChatMessageKind.ToolExecution,
                Content = execution.Name,
                Timestamp = DateTime.Now,
                ToolExecution = model
            };
            Messages.Add(message);
            RecordHistory(message);

            OnPropertyChanged(nameof(HasMessages));
        });
    }

    private void AddUserMessage(string text)
    {
        var message = new ChatMessage
        {
            Role = ChatMessageRole.User,
            Kind = ChatMessageKind.Text,
            Content = text,
            Timestamp = DateTime.Now
        };
        Messages.Add(message);
        RecordHistory(message, refreshList: true);
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(ConversationTitle));
    }

    private static TokenUsageModel? ToUsageModel(AgentTokenUsage? usage) =>
        usage is null
            ? null
            : new TokenUsageModel
            {
                InputTokens = usage.InputTokens,
                OutputTokens = usage.OutputTokens,
                TotalTokens = usage.TotalTokens
            };

    private void AddAssistantMessage(string text, TokenUsageModel? usage = null)
    {
        var message = new ChatMessage
        {
            Role = ChatMessageRole.Assistant,
            Kind = ChatMessageKind.Text,
            Content = text,
            Timestamp = DateTime.Now,
            Usage = usage
        };
        Messages.Add(message);
        RecordHistory(message, refreshList: true);
        OnPropertyChanged(nameof(HasMessages));
    }

    private void AddSystemMessage(string text)
    {
        var message = new ChatMessage
        {
            Role = ChatMessageRole.System,
            Kind = ChatMessageKind.Text,
            Content = text,
            Timestamp = DateTime.Now
        };
        Messages.Add(message);
        RecordHistory(message, refreshList: true);
        OnPropertyChanged(nameof(HasMessages));
    }

    private static string BuildWorkflowDecisionMessage(UserDecisionRequest request)
    {
        var actions = request.AvailableActions.Count == 0
            ? "No predefined actions."
            : string.Join(", ", request.AvailableActions.Select(action => action.Title));

        return
            $"{request.Title}\n\n" +
            $"Operation:\n{request.OperationId}\n\n" +
            $"Details:\n{request.Message}\n\n" +
            $"Available actions:\n{actions}\n\n" +
            "Reply in chat with additional instructions, or choose one of the actions below.";
    }

    private static string? BuildWorkflowStateText(AgentWorkflowEvent workflowEvent)
    {
        return workflowEvent.Kind switch
        {
            AgentWorkflowEventKind.WorkflowStarted => $"{Describe(workflowEvent.OperationType)} started...",
            AgentWorkflowEventKind.WorkflowResumed => $"Resuming {Describe(workflowEvent.OperationType).ToLowerInvariant()}...",
            AgentWorkflowEventKind.WorkflowCancelled => "Operation cancelled.",
            // Failures are reported once, from the operation result.
            _ => null
        };
    }

    private static string Describe(AgentOperationType operationType)
    {
        return operationType switch
        {
            AgentOperationType.Chat => "Analysis",
            AgentOperationType.DocumentAnalysis => "Document processing",
            AgentOperationType.DataProcessing => "Data processing",
            AgentOperationType.DataAnalysis => "Data analysis",
            AgentOperationType.SchemaAnalysis => "Schema analysis",
            AgentOperationType.SchemaModification => "Schema modification",
            _ => "Operation"
        };
    }

    private static string BuildStatus(AgentOperationResult result)
    {
        var state = result.State switch
        {
            ProcessingState.WaitingForUser => "Waiting for your decision",
            ProcessingState.Completed => "Completed",
            ProcessingState.Cancelled => "Cancelled",
            ProcessingState.Failed => result.FailureReason ?? "Failed",
            _ => result.State.ToString()
        };

        // Token counts are reported here too, so a turn that produced no
        // assistant message still shows what it cost.
        return result.Usage is { } usage
            ? $"{state} - {usage.InputTokens:N0} tokens in / {usage.OutputTokens:N0} out"
            : state;
    }

    private void NewChat()
    {
        _logger.LogInformation("New chat started. PreviousConversationId: {ConversationId}", _conversationId);
        Cancel();
        _orchestrator.Reset();
        Messages.Clear();
        _reasoningSegments.Clear();
        _toolCalls.Clear();
        _activeOperation = null;
        _lastRenderedDecisionRequestId = null;
        // A new conversation is registered in history rather than being a bare
        // identifier, so it appears in the sidebar immediately.
        _conversationId = _history.Create().Id;
        Prompt = string.Empty;
        ResponseStatus = "Ready";
        RefreshConversations();
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(ConversationTitle));
    }

    private void Cancel()
    {
        if (_cancellationTokenSource is null)
        {
            return;
        }

        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
        _cancellationTokenSource = null;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class ChatConversation
{
    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public int MessageCount { get; init; }

    public DateTime LastUpdated { get; init; }

    public string Subtitle =>
        MessageCount == 1 ? $"{RelativeDate} - 1 message" : $"{RelativeDate} - {MessageCount} messages";

    public string RelativeDate =>
        LastUpdated.Date == DateTime.Today
            ? "Today"
            : LastUpdated.Date == DateTime.Today.AddDays(-1)
                ? "Yesterday"
                : LastUpdated.ToString("MMM dd");
}
