using TokkDb.Assistant.Agents.Changes;
using TokkDb.Assistant.Agents.Context;
using TokkDb.Assistant.Agents.Models;
using TokkDb.Assistant.Agents.Operations;
using TokkDb.Assistant.Agents.Placement;
using TokkDb.Assistant.Agents.Requests;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Agents.Orchestration;

/// <summary>Everything one turn's flows share: the storage and its recorder, the runner, the request and its tracer.</summary>
internal sealed class TurnContext
{
    public required IStorage Storage { get; init; }
    public required ITraceRecorder Recorder { get; init; }
    public required OperationRunner Runner { get; init; }
    public required ToolCatalog Tools { get; init; }
    public required OrchestratorOptions Options { get; init; }
    public required RequestLifecycle Lifecycle { get; init; }
    public required ChangeClassifier Classifier { get; init; }
    public required ChangeJournal Journal { get; init; }
    public required Placer Placer { get; init; }
    public required WriteGate Gate { get; init; }
    public required Conversation Conversation { get; init; }
    public required RequestTrace Request { get; set; }
    public required Tracer Tracer { get; init; }
    public required TurnInput Input { get; init; }
    public QueryResultHandle? LastHandle { get; init; }
    public CancellationToken Cancellation { get; init; }

    public string Text => Input.Text?.Trim() ?? string.Empty;

    public string Digest() =>
        SchemaDigest.Render(Storage.GetCollectionDefinitions(), Storage.GetRelations(), Options.DigestBudget);

    /// <summary>The prefix of an operation: instructions, the digest where the operation reads one, tools (AG-6a).</summary>
    public string Prefix(OperationDeclaration operation, bool withDigest) =>
        PromptPrefix.Assemble(operation, withDigest ? Digest() : string.Empty, Tools);

    /// <summary>Runs an operation as a step of this request, chained after the last step.</summary>
    public async Task<OperationResult<T>> RunAsync<T>(
        OperationDeclaration operation, string content, EgressClass egress, OutputParser<T> parse, string? summary, bool withDigest = true)
    {
        var result = await Runner.RunAsync(operation,
            new AssembledContext(Prefix(operation, withDigest), content, egress, summary),
            parse, Request.Id, Tracer.Last, Cancellation).ConfigureAwait(false);

        Tracer.Adopt(result.Step);
        return result;
    }

    // ---- How a turn ends ------------------------------------------------------------------------------

    public TurnOutcome Complete(string reply, ResultPage? results = null, string? committedHash = null, string? payload = null)
    {
        Tracer.Note("the answer", output: reply);
        Request = Lifecycle.Complete(Request, committedHash) ?? Recorder.Request(Request.Id) ?? Request;
        Storage.Conversations.Append(Conversation.Id, TurnSpeaker.Assistant, reply, requestId: Request.Id, payload: payload);
        return new TurnOutcome(Conversation.Id, Request.Id, Request.State, reply, Results: results);
    }

    /// <summary>Stops to ask (D-15): the intent is held, durably, and the question is in the conversation.</summary>
    public TurnOutcome Ask(ConfirmationCard card, RequestIntent intent)
    {
        var text = card.Title + "\n" + string.Join("\n", card.Lines) + (card.UndoNote is null ? "" : "\n" + card.UndoNote);
        Tracer.Note("the question", output: text);
        Request = Lifecycle.Hold(Request, intent) ?? Recorder.Request(Request.Id) ?? Request;
        Storage.Conversations.Append(Conversation.Id, TurnSpeaker.Assistant, text, requestId: Request.Id, payload: IntentPayloads.QuestionPayload(Request.Id));
        return new TurnOutcome(Conversation.Id, Request.Id, Request.State, text, Question: card);
    }

    public TurnOutcome Fail(string reason)
    {
        var reply = Replies.Failed(reason);
        Tracer.Note("the answer", output: reply);
        Request = Lifecycle.Fail(Request, reason) ?? Recorder.Request(Request.Id) ?? Request;
        Storage.Conversations.Append(Conversation.Id, TurnSpeaker.Assistant, reply, requestId: Request.Id);
        return new TurnOutcome(Conversation.Id, Request.Id, Request.State, reply, Failure: reason);
    }

    public TurnOutcome Cancel()
    {
        var reply = Replies.Cancelled();
        Request = Lifecycle.Cancel(Request) ?? Recorder.Request(Request.Id) ?? Request;
        Storage.Conversations.Append(Conversation.Id, TurnSpeaker.Assistant, reply, requestId: Request.Id);
        return new TurnOutcome(Conversation.Id, Request.Id, Request.State, reply);
    }
}
