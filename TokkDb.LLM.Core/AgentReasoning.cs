namespace TokkDb.LLM.Core;

/// <summary>
/// One contiguous block of model reasoning ("thinking") output.
///
/// A single turn can produce several segments when the model alternates
/// between reasoning and visible answer text (for example around tool calls).
/// Each segment is rendered as its own collapsible chat message, which is what
/// preserves the relative order of reasoning and answer content.
///
/// This is the provider-independent representation: nothing in this record is
/// specific to OpenAI, Ollama or any other backend, and it never carries
/// system prompts, tool wiring, credentials or endpoint configuration.
/// </summary>
public sealed record AgentReasoningSegment(
    string SegmentId,
    string Text,
    DateTimeOffset TimestampUtc);

/// <summary>
/// Incremental reasoning update. <paramref name="Delta"/> is the newly produced
/// text only; consumers append it to the segment identified by
/// <paramref name="SegmentId"/>. Providers that return reasoning in one piece
/// simply produce a single delta followed by a completion update.
/// </summary>
public sealed record AgentReasoningUpdate(
    string SegmentId,
    string Delta,
    bool IsCompleted,
    DateTimeOffset TimestampUtc);

public sealed class AgentReasoningEventArgs : EventArgs
{
    public AgentReasoningEventArgs(AgentReasoningUpdate update)
    {
        Update = update;
    }

    public AgentReasoningUpdate Update { get; }
}
