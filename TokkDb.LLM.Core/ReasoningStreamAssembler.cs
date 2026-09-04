namespace TokkDb.LLM.Core;

/// <summary>
/// Assembles streamed model reasoning into ordered segments and republishes each
/// delta as a provider-independent <see cref="AgentReasoningUpdate"/>.
///
/// A new segment starts whenever reasoning resumes after visible answer text, so
/// a model that alternates between thinking and answering produces separate
/// collapsible blocks in the correct relative order instead of one merged block.
///
/// The assembler is deliberately free of any provider or framework type: the
/// caller decides what counts as reasoning and what counts as answer text.
/// </summary>
public sealed class ReasoningStreamAssembler
{
    private readonly Action<AgentReasoningUpdate> _publish;
    private readonly List<AgentReasoningSegment> _segments = [];
    private readonly System.Text.StringBuilder _current = new();

    private string? _segmentId;
    private bool _answerTextSinceReasoning;

    public ReasoningStreamAssembler(Action<AgentReasoningUpdate> publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        _publish = publish;
    }

    /// <summary>
    /// Segments completed so far, in the order the model produced them.
    /// </summary>
    public IReadOnlyList<AgentReasoningSegment> Segments => _segments;

    /// <summary>
    /// Records a reasoning delta. Empty deltas are ignored so that providers
    /// which emit padding do not create empty blocks.
    /// </summary>
    public void AppendReasoning(string? delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        if (_segmentId is null || _answerTextSinceReasoning)
        {
            Complete();
            _segmentId = Guid.NewGuid().ToString("N");
            _answerTextSinceReasoning = false;
        }

        _current.Append(delta);
        _publish(new AgentReasoningUpdate(_segmentId, delta, false, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Records that visible answer text was produced. The next reasoning delta
    /// therefore belongs to a new segment.
    /// </summary>
    public void NoteAnswerText()
    {
        if (_segmentId is not null)
        {
            _answerTextSinceReasoning = true;
        }
    }

    /// <summary>
    /// Closes the open segment, if any. Safe to call repeatedly.
    /// </summary>
    public void Complete()
    {
        if (_segmentId is null)
        {
            return;
        }

        _segments.Add(new AgentReasoningSegment(_segmentId, _current.ToString(), DateTimeOffset.UtcNow));
        _publish(new AgentReasoningUpdate(_segmentId, string.Empty, true, DateTimeOffset.UtcNow));

        _segmentId = null;
        _current.Clear();
    }
}
