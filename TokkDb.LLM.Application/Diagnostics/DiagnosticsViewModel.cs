using System.Collections.ObjectModel;
using System.Windows.Input;
using TokkDb.LLM.Core.Diagnostics;
using TokkDb.LLM.Core.Orchestration;

namespace TokkDb.LLM.Application.Diagnostics;

public sealed class DiagnosticsViewModel : BindableObject
{
    private readonly IAgentOrchestrator _orchestrator;
    private readonly IDiagnosticsService _diagnosticsService;
    private DiagnosticEventViewModel? _selectedEvent;
    private string? _searchText;

    private string _storageStatus = "Healthy";
    private string _storageDetails = "Storage runtime ready";

    private string _llmStatus = "Not tested";
    private string _llmDetails = "No active connection";

    private string _agentStatus = "Ready";
    private string _agentDetails = "Conversation agent initialized";

    private string _workflowStatus = "Idle";
    private string _workflowDetails = "No active workflow";


    public DiagnosticsViewModel(IAgentOrchestrator orchestrator, IDiagnosticsService diagnosticsService)
    {
        _orchestrator = orchestrator;
        _diagnosticsService = diagnosticsService;
        RefreshCommand = new Command(Refresh);

        ClearLogsCommand = new Command(ClearLogs);

        AddSeedEvents();

        ApplyFilter();
        
        // The orchestration layer already writes tool and workflow diagnostics
        // to IDiagnosticsService; here we only mirror workflow state into the
        // status panel and refresh the visible log.
        _diagnosticsService.EventAdded += OnDiagnosticEventAdded;
        _orchestrator.WorkflowEventRaised += OnWorkflowEventRaised;
    }

    private void OnDiagnosticEventAdded(object? sender, DiagnosticEvent diagnosticEvent)
    {
        AddEvent(diagnosticEvent);
    }

    private void OnWorkflowEventRaised(object? sender, AgentWorkflowEventArgs e)
    {
        var workflowEvent = e.WorkflowEvent;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            WorkflowStatus = workflowEvent.Kind switch
            {
                AgentWorkflowEventKind.WorkflowStarted => "Running",
                AgentWorkflowEventKind.WorkflowProgress => "Running",
                AgentWorkflowEventKind.WorkflowResumed => "Resuming",
                AgentWorkflowEventKind.WorkflowWaitingForUser => "Waiting for user",
                AgentWorkflowEventKind.WorkflowCompleted => "Completed",
                AgentWorkflowEventKind.WorkflowCancelled => "Cancelled",
                AgentWorkflowEventKind.WorkflowFailed => "Failed",
                _ => WorkflowStatus
            };

            WorkflowDetails =
                $"{workflowEvent.OperationType} - {workflowEvent.Message} (operation {workflowEvent.OperationId})";

            AgentStatus = workflowEvent.Kind == AgentWorkflowEventKind.WorkflowFailed ? "Error" : "Ready";
            AgentDetails = workflowEvent.Details ?? workflowEvent.Message;
        });
    }

    // =========================================================
    // COMMANDS
    // =========================================================

    public ICommand RefreshCommand { get; }

    public ICommand ClearLogsCommand { get; }


    // =========================================================
    // EVENTS
    // =========================================================

    public ObservableCollection<DiagnosticEventViewModel> Events { get; }
        = new();

    public ObservableCollection<DiagnosticEventViewModel> FilteredEvents { get; }
        = new();


    public DiagnosticEventViewModel? SelectedEvent
    {
        get => _selectedEvent;

        set
        {
            if (_selectedEvent == value)
                return;

            _selectedEvent = value;

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedEvent));
            OnPropertyChanged(nameof(IsNoEventSelected));
        }
    }


    public bool HasSelectedEvent =>
        SelectedEvent is not null;

    public bool IsNoEventSelected =>
        SelectedEvent is null;


    public string? SearchText
    {
        get => _searchText;

        set
        {
            if (_searchText == value)
                return;

            _searchText = value;

            OnPropertyChanged();

            ApplyFilter();
        }
    }


    public string EventsCountText =>
        $"{FilteredEvents.Count} events";


    // =========================================================
    // STATUS
    // =========================================================

    public string StorageStatus
    {
        get => _storageStatus;
        set
        {
            _storageStatus = value;
            OnPropertyChanged();
        }
    }

    public string StorageDetails
    {
        get => _storageDetails;
        set
        {
            _storageDetails = value;
            OnPropertyChanged();
        }
    }


    public string LlmStatus
    {
        get => _llmStatus;
        set
        {
            _llmStatus = value;
            OnPropertyChanged();
        }
    }

    public string LlmDetails
    {
        get => _llmDetails;
        set
        {
            _llmDetails = value;
            OnPropertyChanged();
        }
    }


    public string AgentStatus
    {
        get => _agentStatus;
        set
        {
            _agentStatus = value;
            OnPropertyChanged();
        }
    }

    public string AgentDetails
    {
        get => _agentDetails;
        set
        {
            _agentDetails = value;
            OnPropertyChanged();
        }
    }


    public string WorkflowStatus
    {
        get => _workflowStatus;
        set
        {
            _workflowStatus = value;
            OnPropertyChanged();
        }
    }

    public string WorkflowDetails
    {
        get => _workflowDetails;
        set
        {
            _workflowDetails = value;
            OnPropertyChanged();
        }
    }


    // =========================================================
    // FILTER
    // =========================================================

    private void ApplyFilter()
    {
        FilteredEvents.Clear();

        var search = SearchText?.Trim();

        var result =
            string.IsNullOrWhiteSpace(search)
                ? Events
                : Events.Where(x =>
                    x.Title.Contains(
                        search,
                        StringComparison.OrdinalIgnoreCase)
                    ||
                    x.Summary.Contains(
                        search,
                        StringComparison.OrdinalIgnoreCase)
                    ||
                    x.Source.Contains(
                        search,
                        StringComparison.OrdinalIgnoreCase));


        foreach (var diagnosticEvent in result)
        {
            FilteredEvents.Add(diagnosticEvent);
        }

        OnPropertyChanged(nameof(EventsCountText));
    }


    // =========================================================
    // COMMANDS
    // =========================================================

    private void Refresh()
    {
        StorageStatus = "Healthy";
        StorageDetails = "Storage runtime ready";

        AgentStatus = "Ready";
        AgentDetails = "Conversation agent initialized";

        WorkflowStatus = "Idle";
        WorkflowDetails = "No active workflow";

        AddEvent(new DiagnosticEvent(
            DateTime.UtcNow,
            DiagnosticLevel.Information,
            "Diagnostics",
            "System",
            "Diagnostics refreshed",
            "System status information was refreshed."));
    }


    private void ClearLogs()
    {
        Events.Clear();
        FilteredEvents.Clear();

        SelectedEvent = null;

        OnPropertyChanged(nameof(EventsCountText));
    }


    // =========================================================
    // PUBLIC API
    // =========================================================

    public void AddEvent(DiagnosticEvent diagnosticEvent)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Events.Insert(
                0,
                new DiagnosticEventViewModel(diagnosticEvent));

            ApplyFilter();
        });
    }


    // =========================================================
    // SEED DATA
    // =========================================================

    private void AddSeedEvents()
    {
        AddEvent(new DiagnosticEvent(
            DateTime.UtcNow.AddMinutes(-2),
            DiagnosticLevel.Information,
            "Agent",
            "Tool",
            "QueryRecords",
            "QueryRecords completed successfully.",
            """
            Collection: Customer
            Records returned: 12
            """));


        AddEvent(new DiagnosticEvent(
            DateTime.UtcNow.AddMinutes(-5),
            DiagnosticLevel.Warning,
            "LLM",
            "Request",
            "Slow LLM response",
            "The LLM request took longer than expected.",
            "Response time: 8.2 seconds"));


        AddEvent(new DiagnosticEvent(
            DateTime.UtcNow.AddMinutes(-10),
            DiagnosticLevel.Information,
            "Workflow",
            "Processing",
            "Workflow completed",
            "Processing workflow completed successfully."));
    }
}
