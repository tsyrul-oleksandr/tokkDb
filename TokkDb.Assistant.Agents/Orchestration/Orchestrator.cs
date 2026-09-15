using TokkDb.Assistant.Agents.Changes;
using TokkDb.Assistant.Agents.Context;
using TokkDb.Assistant.Agents.Models;
using TokkDb.Assistant.Agents.Operations;
using Catalogue = TokkDb.Assistant.Agents.Operations.Operations;
using TokkDb.Assistant.Agents.Placement;
using TokkDb.Assistant.Agents.Requests;
using TokkDb.Assistant.Ingestion;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Agents.Orchestration;

/// <summary>
/// The orchestrator (AG-2 to AG-11, D-4 to D-8, D-15, step 4.4): a person says something, the
/// intent is worked out - without a model where the signal is unambiguous - and the request runs
/// as a traced, resumable state machine to a reply, a question, or a stated failure.
///
/// Every model call goes through the <see cref="OperationRunner"/>; every write goes through the
/// <see cref="WriteGate"/> and inside one unit of work with its change records; every question
/// holds the validated intent and is answered through <see cref="AnswerAsync"/>, which executes
/// exactly what was shown. On startup, <see cref="RecoverAsync"/> applies AG-8b's three paths.
/// </summary>
public sealed class Orchestrator
{
    private readonly IStorage _storage;
    private readonly ITraceRecorder _recorder;
    private readonly OperationRunner _runner;
    private readonly ToolCatalog _tools;
    private readonly OrchestratorOptions _options;
    private readonly RequestLifecycle _lifecycle;
    private readonly ChangeClassifier _classifier;
    private readonly ChangeJournal _journal;
    private readonly Placer _placer;
    private readonly WriteGate _gate = new();

    public Orchestrator(IStorage storage, ITraceRecorder recorder, OperationRunner runner, ToolCatalog tools, OrchestratorOptions? options = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _options = options ?? new OrchestratorOptions();
        _lifecycle = new RequestLifecycle(_recorder);
        _classifier = new ChangeClassifier(_storage);
        _journal = new ChangeJournal(_storage, _recorder, _classifier);
        _placer = new Placer(_storage, _classifier);
    }

    public IStorage Storage => _storage;
    public ITraceRecorder Recorder => _recorder;
    public OrchestratorOptions Options => _options;
    public ChangeClassifier Classifier => _classifier;

    /// <summary>What is being written right now, for the interface to say busy (AG-10).</summary>
    public string? Busy => _gate.Busy;

    private TurnContext Context(Conversation conversation, RequestTrace request, TurnInput input, CancellationToken cancellation) => new()
    {
        Storage = _storage, Recorder = _recorder, Runner = _runner, Tools = _tools, Options = _options,
        Lifecycle = _lifecycle, Classifier = _classifier, Journal = _journal, Placer = _placer, Gate = _gate,
        Conversation = conversation, Request = request, Tracer = new Tracer(_recorder, request), Input = input,
        LastHandle = LastHandle(conversation.Id), Cancellation = cancellation
    };

    /// <summary>The last result shown in a conversation, if any (QR-3a).</summary>
    public QueryResultHandle? LastHandle(Ulid conversationId)
    {
        foreach (var turn in _storage.Conversations.Turns(conversationId).Reverse())
        {
            if (turn.Speaker is not TurnSpeaker.Assistant) continue;
            if (QueryResultHandle.TryRead(turn.Payload) is { } handle) return handle;
        }

        return null;
    }

    /// <summary>One turn: what the person said, through to what the assistant did or asked.</summary>
    public async Task<TurnOutcome> HandleAsync(TurnInput input, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var conversation = input.ConversationId is { } id
            ? _storage.Conversations.Get(id) ?? throw new UnknownConversationException(id)
            : _storage.Conversations.Start(Title(input));

        // Files already read are the files; paths beside them are their references for the turn
        // (SC-10), not a second reading of the same file.
        var files = new List<ParsedFile>(input.ReadFiles);
        if (files.Count == 0)
        {
            foreach (var path in input.AttachmentPaths) files.Add(FileParsers.Read(path));
        }

        var request = _lifecycle.Begin(conversation.Id, Title(input));
        _storage.Conversations.Append(conversation.Id, TurnSpeaker.Person, input.Text ?? string.Empty, input.AttachmentPaths, request.Id);

        var ctx = Context(conversation, request, input, cancellation);

        ctx.Tracer.Note("the person", input: (input.Text ?? "") + (files.Count > 0 ? $" [{string.Join(", ", files.Select(static file => file.Name + file.Extension))}]" : ""));
        ctx.Tracer.Note("the assistant", output: "work out what is meant, then do it and show what was done");

        try
        {
            var intent = await IntentAsync(ctx, files).ConfigureAwait(false);

            return intent switch
            {
                IntentKind.Store => await StoreFlow.HandleAsync(ctx, files).ConfigureAwait(false),
                IntentKind.Find => await RetrievalFlow.HandleAsync(ctx, ctx.Text).ConfigureAwait(false),
                IntentKind.FollowUp when ctx.LastHandle is { } handle => await FollowUpFlow.HandleAsync(ctx, handle, ctx.Text).ConfigureAwait(false),
                IntentKind.FollowUp => await RetrievalFlow.HandleAsync(ctx, ctx.Text).ConfigureAwait(false),
                IntentKind.Correct => await CorrectionFlow.HandleAsync(ctx, ctx.Text).ConfigureAwait(false),
                IntentKind.Remove => await RestructureFlow.RemoveAsync(ctx, ctx.Text).ConfigureAwait(false),
                IntentKind.Restructure => await RestructureFlow.HandleAsync(ctx, ctx.Text).ConfigureAwait(false),
                IntentKind.Undo => await UndoFlow.HandleAsync(ctx, ctx.Text).ConfigureAwait(false),
                _ => ctx.Complete("I can keep things you give me, find them, correct them, remove them, take a change back, or change what a thing keeps. Which would you like?")
            };
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // N-7: nothing half-applied - every write is one unit of work - and the state is Cancelled.
            return ctx.Cancel();
        }
        catch (ModelFailedException failed)
        {
            // N-6: the model failed past the bound or in time; the reason names what could not be done.
            return ctx.Fail(failed.Reason);
        }
        catch (Exception failure) when (failure is StorageException or ContextBudgetExceededException or EgressExceededException or InvalidProposalException or NotSupportedException)
        {
            return ctx.Fail(failure.Message);
        }
    }

    /// <summary>
    /// A change made from the browser (BR-8): the same classification, the same card in the
    /// conversation, the same trace and the same diagram entry as the change said in words - with
    /// no model call, because what is meant is already known. What the person did is recorded as
    /// their turn, in words, so the conversation reads as one story.
    /// </summary>
    public async Task<TurnOutcome> ChangeAsync(Ulid? conversationId, StructuralAction action, string said, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(said);

        var conversation = conversationId is { } id
            ? _storage.Conversations.Get(id) ?? throw new UnknownConversationException(id)
            : _storage.Conversations.Start(said);

        var request = _lifecycle.Begin(conversation.Id, said);
        _storage.Conversations.Append(conversation.Id, TurnSpeaker.Person, said, requestId: request.Id);

        var ctx = Context(conversation, request, new TurnInput(conversation.Id, said), cancellation);
        ctx.Tracer.Note("the person", input: said + " (from the browser)");
        ctx.Tracer.Note("the assistant", output: "what is meant is already known; do it and show what was done");

        try
        {
            return await RestructureFlow.ApplyAsync(ctx, action).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return ctx.Cancel();
        }
        catch (Exception failure) when (failure is StorageException or NotSupportedException or ArgumentException)
        {
            return ctx.Fail(failure.Message);
        }
    }

    /// <summary>
    /// A record opened in the browser becomes the subject of the next thing said (BR-9): the
    /// conversation is told, as a turn that shows the record, and the turn carries the same handle
    /// a result would, so that "tell me more about this" is a follow-up about it. No request and no
    /// model call: nothing was asked.
    /// </summary>
    public (Conversation Conversation, ResultPage Shown)? LookAt(Ulid? conversationId, string thing, Ulid id)
    {
        var definition = _storage.GetCollectionDefinition(thing);
        if (definition is null) return null;

        var record = _storage.GetById(definition.Name, id);
        if (record is null) return null;

        var conversation = conversationId is { } known
            ? _storage.Conversations.Get(known) ?? throw new UnknownConversationException(known)
            : _storage.Conversations.Start("Looking at " + DisplayValue.For(definition, record));

        var query = new StorageQuery(definition.Name, ids: [id], take: 1).Computing(new QueryAggregate(AggregateFunction.Count));
        var result = _storage.ExecuteQuery(query);
        var title = DisplayValue.For(definition, record);
        var handle = new QueryResultHandle(
            definition.Name,
            QueryJson.Write(query),
            1,
            [.. definition.Columns.Select(static column => column.Name)],
            new Dictionary<string, string>(StringComparer.Ordinal),
            [new ShownRecord(id, _storage.HeadVersion(definition.Name, id) ?? Ulid.Empty, title)],
            null,
            null,
            _options.Clock());

        var text = $"You opened {title} in the browser.";
        _storage.Conversations.Append(conversation.Id, TurnSpeaker.Assistant, text, payload: handle.Write());

        return (conversation, new ResultPage(definition.Name, result.Records, [title], 1, result.Aggregates, result.Execution, handle));
    }

    /// <summary>
    /// What the person could say next (UI-9): a request of its own, so that the call is traced
    /// and budgeted like any other (TR-3, AG-1), but no turn of the conversation - nothing was
    /// said - and no reply. The model's list is cleaned by C#, and when the model fails the
    /// options are C#'s own, so the control never comes back empty.
    /// </summary>
    public async Task<SuggestionSet> SuggestAsync(Ulid? conversationId, string? draft, CancellationToken cancellation = default)
    {
        var turns = conversationId is { } id ? _storage.Conversations.Turns(id) : [];
        var request = _lifecycle.Begin(conversationId ?? Ulid.Empty, "suggestions");
        var lastShown = conversationId is { } known ? LastHandle(known) : null;
        var somethingToTakeBack = turns.Any(turn => turn.RequestId is { } requestId && _recorder.Changes(requestId).Count > 0);
        var fallback = Suggestions.Starters(_storage, lastShown, somethingToTakeBack, draft);

        try
        {
            var prefix = PromptPrefix.Assemble(Catalogue.Suggestions, string.Empty, _tools);
            var content = Suggestions.Content(_storage, turns, draft);
            var result = await _runner.RunAsync(Catalogue.Suggestions, new AssembledContext(prefix, content, EgressClass.BoundedSample, "what to say next"),
                Answers.Suggestions(), request.Id, null, cancellation).ConfigureAwait(false);

            var options = Suggestions.Clean(result.Value, fallback);
            _lifecycle.Complete(request);
            return new SuggestionSet(options, FromModel: options.Count > 0 && result.Value.Any(option => options.Contains(option.Trim().Trim('"'), StringComparer.OrdinalIgnoreCase)), request.Id);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _lifecycle.Cancel(request);
            throw;
        }
        catch (Exception failure) when (failure is ModelFailedException or ContextBudgetExceededException or EgressExceededException or StorageException)
        {
            _lifecycle.Fail(request, failure.Message);
            return new SuggestionSet(Suggestions.Clean([], fallback), FromModel: false, request.Id);
        }
    }

    /// <summary>
    /// The answer to a question (D-15, AG-8, AG-8a): yes claims the request once and executes
    /// exactly what was shown, with no further model call; no completes it with the refusal in
    /// the trace (N-8). A second answer to the same question finds the request moved and does nothing.
    /// </summary>
    public async Task<TurnOutcome> AnswerAsync(Ulid requestId, bool yes, CancellationToken cancellation = default)
    {
        var request = _recorder.Request(requestId) ?? throw new InvalidOperationException($"There is no request {requestId}.");
        var conversation = _storage.Conversations.Get(request.ConversationId) ?? throw new UnknownConversationException(request.ConversationId);

        if (request.State is not RequestState.WaitingForUser)
        {
            return new TurnOutcome(conversation.Id, request.Id, request.State, request.State is RequestState.Completed ? Replies.AlreadyDone() : "That question is no longer open.");
        }

        if (!yes)
        {
            var declined = _lifecycle.Decline(request) ?? _recorder.Request(requestId)!;
            new Tracer(_recorder, declined).Note("the answer", input: "no", output: Replies.Declined());
            _storage.Conversations.Append(conversation.Id, TurnSpeaker.Assistant, Replies.Declined(), requestId: request.Id);
            return new TurnOutcome(conversation.Id, request.Id, declined.State, Replies.Declined());
        }

        var claimed = _lifecycle.Claim(request);
        if (claimed is null)
        {
            var moved = _recorder.Request(requestId)!;
            return new TurnOutcome(conversation.Id, request.Id, moved.State, "That was already answered.");
        }

        return await ResumeAsync(claimed, conversation, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Startup (AG-8b, TR-4b): every unfinished request is reconciled, and one claimed for
    /// resumption is redone from its held intent - with no model call, and idempotently.
    /// </summary>
    public async Task<IReadOnlyList<(RecoveredRequest Recovered, TurnOutcome? Outcome)>> RecoverAsync(CancellationToken cancellation = default)
    {
        var results = new List<(RecoveredRequest, TurnOutcome?)>();

        foreach (var recovered in StartupRecovery.Run(_recorder))
        {
            TurnOutcome? outcome = null;

            if (recovered.NeedsResumption && _storage.Conversations.Get(recovered.After.ConversationId) is { } conversation)
            {
                outcome = await ResumeAsync(recovered.After, conversation, cancellation).ConfigureAwait(false);
            }

            results.Add((recovered, outcome));
        }

        return results;
    }

    private async Task<TurnOutcome> ResumeAsync(RequestTrace claimed, Conversation conversation, CancellationToken cancellation)
    {
        var tracer = new Tracer(_recorder, claimed);
        var last = _recorder.Read(claimed.Id)?.Steps.LastOrDefault();
        if (last is not null) tracer.Adopt(last);

        var ctx = new TurnContext
        {
            Storage = _storage, Recorder = _recorder, Runner = _runner, Tools = _tools, Options = _options,
            Lifecycle = _lifecycle, Classifier = _classifier, Journal = _journal, Placer = _placer, Gate = _gate,
            Conversation = conversation, Request = claimed, Tracer = tracer, Input = new TurnInput(conversation.Id, claimed.Operation),
            LastHandle = LastHandle(conversation.Id), Cancellation = cancellation
        };

        tracer.Note("the answer", input: "yes", output: "do what was shown");

        try
        {
            return claimed.Intent?.Kind switch
            {
                IntentPayloads.PlacementKind => await StoreFlow.ResumeAsync(ctx).ConfigureAwait(false),
                IntentPayloads.StructureKind => await RestructureFlow.ResumeAsync(ctx).ConfigureAwait(false),
                IntentPayloads.PartialUndoKind => await UndoFlow.ResumeAsync(ctx).ConfigureAwait(false),
                _ => ctx.Fail("the request holds nothing that can be resumed")
            };
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return ctx.Cancel();
        }
        catch (Exception failure) when (failure is StorageException or NotSupportedException or InvalidProposalException or InvalidOperationException)
        {
            return ctx.Fail(failure.Message);
        }
    }

    private async Task<IntentKind> IntentAsync(TurnContext ctx, IReadOnlyList<ParsedFile> files)
    {
        var hasTable = files.Any(static file => file.IsTabular);
        var hasProse = files.Any(static file => file.IsProse);

        if (Intents.Deterministic(ctx.Input, hasTable, hasProse, ctx.LastHandle is not null, out var reason) is { } decided)
        {
            ctx.Tracer.Note("what was meant", input: reason, output: decided.ToString().ToLowerInvariant());
            return decided;
        }

        if (ctx.Text.Length == 0)
        {
            ctx.Tracer.Note("what was meant", input: "an empty message", output: "nothing");
            return IntentKind.Other;
        }

        var content = "message: " + ctx.Text + (files.Count > 0 ? $"\nattached: {string.Join(", ", files.Select(static file => file.Extension))}" : "")
                      + (ctx.LastHandle is not null ? "\n(some records were just shown)" : "");
        var result = await ctx.RunAsync(Catalogue.Intent, content, EgressClass.Nothing, Answers.Intent, summary: ctx.Text, withDigest: false).ConfigureAwait(false);

        var intent = Intents.FromWord(result.Value);
        if (intent is IntentKind.Find && ctx.LastHandle is not null && Intents.Deterministic(ctx.Input, false, false, true, out _) is IntentKind.FollowUp)
        {
            intent = IntentKind.FollowUp;
        }

        return intent;
    }

    private static string Title(TurnInput input)
    {
        var text = (input.Text ?? "").ReplaceLineEndings(" ").Trim();
        if (text.Length == 0 && input.HasAttachments)
        {
            text = input.ReadFiles.Count > 0 ? input.ReadFiles[0].Name + input.ReadFiles[0].Extension : Path.GetFileName(input.AttachmentPaths[0]);
        }

        if (text.Length == 0) text = "an empty message";

        return text.Length <= 60 ? text : text[..59] + "…";
    }
}
