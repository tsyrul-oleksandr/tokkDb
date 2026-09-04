namespace TokkDb.LLM.Core;

public interface IConversationAgent
{
    event EventHandler<AgentToolExecutionEventArgs>? ToolExecutionStatusChanged;

    /// <summary>
    /// Raised while the model streams reasoning output. Providers that do not
    /// return reasoning simply never raise this event.
    /// </summary>
    event EventHandler<AgentReasoningEventArgs>? ReasoningUpdated;

    /// <summary>
    /// Raised when the agent asks for records to be displayed. Carries the
    /// resolved, provider-independent display model - never raw model text.
    /// </summary>
    event EventHandler<RecordsDisplayEventArgs>? RecordsDisplayRequested;

    Task<ConversationResponse> SendAsync(ConversationRequest request, CancellationToken cancellationToken = default);

    void ResetConversation();
}
