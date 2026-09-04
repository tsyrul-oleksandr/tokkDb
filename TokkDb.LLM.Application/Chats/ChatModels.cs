using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Input;
using TokkDb.LLM.Core;

namespace TokkDb.LLM.Application.Chats;

public enum ChatMessageRole
{
    User,
    Assistant,
    System,
    Workflow
}

public enum ChatMessageKind
{
    Text,
    ToolExecution,
    Workflow,
    Reasoning,
    Records
}

public sealed class ChatMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public ChatMessageRole Role { get; init; }

    public ChatMessageKind Kind { get; init; } = ChatMessageKind.Text;

    public string Content { get; init; } = string.Empty;

    public DateTime Timestamp { get; init; } = DateTime.Now;

    public bool IsUser => Role == ChatMessageRole.User;

    public bool IsAssistant => Role == ChatMessageRole.Assistant;

    public bool IsSystem => Role == ChatMessageRole.System;

    public bool IsWorkflowRole => Role == ChatMessageRole.Workflow;

    public bool IsToolExecution => Kind == ChatMessageKind.ToolExecution;

    public bool IsWorkflow => Kind == ChatMessageKind.Workflow;

    public bool IsReasoning => Kind == ChatMessageKind.Reasoning;

    /// <summary>
    /// True only for an ordinary assistant answer. Tool-execution and reasoning
    /// messages also carry the assistant role, so the plain answer bubble binds
    /// to this instead of <see cref="IsAssistant"/> to avoid rendering twice.
    /// </summary>
    public bool IsAssistantText => IsAssistant && Kind == ChatMessageKind.Text;

    public ToolExecutionModel? ToolExecution { get; init; }

    public WorkflowModel? Workflow { get; init; }

    public ReasoningModel? Reasoning { get; init; }

    public RecordsDisplayModel? Records { get; init; }

    /// <summary>Tokens the turn that produced this message consumed, when reported.</summary>
    public TokenUsageModel? Usage { get; init; }

    public bool HasUsage => Usage is not null;

    public bool IsRecords => Kind == ChatMessageKind.Records;
}

/// <summary>
/// A record list rendered in the chat.
///
/// Built entirely from the application's structured
/// <see cref="RecordsDisplayMessage"/>; no assistant text is parsed to produce
/// it, and no storage access happens from the view.
/// </summary>
public sealed class RecordsDisplayModel
{
    public string CollectionName { get; init; } = string.Empty;

    public IReadOnlyList<RecordDisplayItemModel> Records { get; init; }
        = Array.Empty<RecordDisplayItemModel>();

    /// <summary>
    /// Fields the agent asked for. Kept alongside the rendered values because
    /// the export needs the request, not the rendered result.
    /// </summary>
    public IReadOnlyList<string> RequestedAdditionalFields { get; init; }
        = Array.Empty<string>();

    public bool HasRecords => Records.Count > 0;

    public bool IsEmpty => Records.Count == 0;

    public string Title => CollectionName;

    public string EmptyText => "No records found.";

    /// <summary>Shown when some ids or fields the model supplied could not be used.</summary>
    public string? Notice { get; init; }

    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);
}

/// <summary>
/// One row of a record list. <see cref="DisplayValue"/> comes from the
/// collection's DisplayRule, evaluated by the application.
/// </summary>
public sealed class RecordDisplayItemModel
{
    public string RecordId { get; init; } = string.Empty;

    public string CollectionName { get; init; } = string.Empty;

    public string DisplayValue { get; init; } = string.Empty;

    public IReadOnlyList<RecordFieldModel> AdditionalFields { get; init; }
        = Array.Empty<RecordFieldModel>();

    public bool HasAdditionalFields => AdditionalFields.Count > 0;

    /// <summary>Raised through the navigation abstraction, not by touching the Database page.</summary>
    public ICommand? OpenCommand { get; init; }
}

/// <summary>
/// Token counts shown under an assistant reply: what the request cost to send,
/// and what the reply cost to produce.
/// </summary>
public sealed class TokenUsageModel
{
    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public long TotalTokens { get; init; }

    public string Summary =>
        $"{InputTokens:N0} tokens in / {OutputTokens:N0} out - {TotalTokens:N0} total";
}

public sealed class RecordFieldModel
{
    public string Name { get; init; } = string.Empty;

    public string Value { get; init; } = string.Empty;

    public string Label => $"{Name}: {Value}";
}

/// <summary>
/// Collapsible model reasoning ("thinking") block.
///
/// Content grows while the model streams. The block starts collapsed so that
/// reasoning does not clutter the conversation, and the user expands it on
/// demand. This holds only reasoning text produced by the model - never system
/// prompts, tool wiring, credentials or endpoint configuration.
/// </summary>
public sealed class ReasoningModel : ObservableObject
{
    private string _content = string.Empty;

    // Reasoning is shown while it is being produced, so the user can watch the
    // model work, and folded away once it finishes.
    private bool _isExpanded = true;
    private bool _isStreaming = true;
    private bool _expansionChosenByUser;

    public ReasoningModel()
    {
        ToggleCommand = new Command(() =>
        {
            // Once the user has taken a side, stop overriding them.
            _expansionChosenByUser = true;
            IsExpanded = !IsExpanded;
        });
    }

    public string SegmentId { get; init; } = string.Empty;

    public ICommand ToggleCommand { get; }

    public string Content
    {
        get => _content;
        private set
        {
            if (SetProperty(ref _content, value))
            {
                OnPropertyChanged(nameof(HasContent));
            }
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ToggleIcon));
            }
        }
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        set
        {
            if (!SetProperty(ref _isStreaming, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HeaderText));

            // Thinking has finished: collapse it, unless the user already
            // expanded or collapsed this block themselves.
            if (!value && !_expansionChosenByUser)
            {
                IsExpanded = false;
            }
        }
    }

    public bool HasContent => !string.IsNullOrWhiteSpace(_content);

    public string HeaderText => IsStreaming ? "Thinking..." : "Reasoning";

    public string ToggleIcon => IsExpanded ? "\u25BE" : "\u25B8";

    public void Append(string delta)
    {
        if (!string.IsNullOrEmpty(delta))
        {
            Content = _content + delta;
        }
    }
}

/// <summary>
/// One AI tool call rendered in the chat.
///
/// A single instance covers the whole lifetime of the call: it is created when
/// the tool starts and updated in place when it succeeds or fails, so the
/// conversation keeps one entry per call in chronological order rather than
/// appending a second message for the outcome.
///
/// All payload strings arrive already formatted and redacted from the Core
/// layer; this model performs no serialization and knows nothing about the
/// Agent Framework or any provider.
/// </summary>
public sealed class ToolExecutionModel : ObservableObject
{
    private string _status = nameof(AgentToolExecutionStatus.Started);
    private string? _arguments;
    private string? _response;
    private string? _error;
    private bool _isExpanded;

    public ToolExecutionModel()
    {
        ToggleCommand = new Command(() => IsExpanded = !IsExpanded);
    }

    public string CallId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public DateTimeOffset TimestampUtc { get; init; }

    public ICommand ToggleCommand { get; }

    public string Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusIcon));
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(IsCompleted));
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(IsFailed));
            }
        }
    }

    /// <summary>Formatted request body, or null when the tool takes no arguments.</summary>
    public string? Arguments
    {
        get => _arguments;
        set
        {
            if (SetProperty(ref _arguments, value))
            {
                OnPropertyChanged(nameof(HasArguments));
            }
        }
    }

    public string? Response
    {
        get => _response;
        set
        {
            if (SetProperty(ref _response, value))
            {
                OnPropertyChanged(nameof(HasResponse));
            }
        }
    }

    public string? Error
    {
        get => _error;
        set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ToggleIcon));
            }
        }
    }

    public bool IsCompleted =>
        string.Equals(_status, nameof(AgentToolExecutionStatus.Succeeded), StringComparison.OrdinalIgnoreCase);

    public bool IsRunning =>
        string.Equals(_status, nameof(AgentToolExecutionStatus.Started), StringComparison.OrdinalIgnoreCase);

    public bool IsFailed =>
        string.Equals(_status, nameof(AgentToolExecutionStatus.Failed), StringComparison.OrdinalIgnoreCase);

    /// <summary>The clickable word shown next to the tool name.</summary>
    public string StatusText =>
        IsCompleted ? "Completed" :
        IsFailed ? "Error" :
        "Started";

    public string StatusIcon =>
        IsCompleted ? "\u2713" :
        IsFailed ? "!" :
        "\u25CF";

    public Color StatusColor =>
        IsCompleted ? Color.FromArgb("#1F8B4C") :
        IsFailed ? Color.FromArgb("#C0392B") :
        Color.FromArgb("#3B6FD4");

    public bool HasArguments => !string.IsNullOrWhiteSpace(_arguments);

    public bool HasResponse => !string.IsNullOrWhiteSpace(_response);

    public bool HasError => !string.IsNullOrWhiteSpace(_error);

    public string ToggleIcon => IsExpanded ? "\u25BE" : "\u25B8";

    /// <summary>
    /// Applies a later transition of the same call.
    /// </summary>
    public void Apply(AgentToolExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);

        if (execution.Arguments is not null)
        {
            Arguments = execution.Arguments;
        }

        if (execution.Response is not null)
        {
            Response = execution.Response;
        }

        if (execution.Error is not null)
        {
            Error = execution.Error;
        }

        Status = execution.Status.ToString();
    }
}

public sealed class WorkflowModel
{
    public string WorkflowOperationId { get; init; } = string.Empty;

    public string WorkflowStatus { get; init; } = string.Empty;

    public UserDecisionRequest? DecisionRequest { get; init; }

    public IReadOnlyList<WorkflowActionModel> AvailableActions { get; init; }
        = Array.Empty<WorkflowActionModel>();

    public string Message { get; init; } = string.Empty;

    public bool HasActions => AvailableActions.Count > 0;
}

public sealed class WorkflowActionModel
{
    public string WorkflowOperationId { get; init; } = string.Empty;

    public WorkflowAction Action { get; init; } = default!;

    public string Title => Action.Title;

    public string Description => Action.Description ?? string.Empty;

    public WorkflowDecision Decision => Action.Decision;

    public string ActionId => Action.ActionId;
}
