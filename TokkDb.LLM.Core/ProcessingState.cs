namespace TokkDb.LLM.Core;

public enum ProcessingState
{
    Running,
    Resuming,
    WaitingForUser,
    Completed,
    Cancelled,
    Failed,

    // Legacy states kept for backward compatibility with older persisted contexts.
    Created,
    Analyzing,
    ExtractingData,
    ApplyingChanges,
    SavingData,
    ValidatingData
}
