using TokkDb.Assistant.Trace;
using TokkDb.Documents;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Values;
using EngineColumn = TokkDb.Pages.ColumnDescriptor;
using EngineConnection = TokkDb.TokkDbConnection;

namespace TokkDb.Assistant.Storage.Engine;

/// <summary>
/// <see cref="ITraceRecorder"/> over the engine's reserved collections (D-2, D-8, step 3.2).
///
/// <b>Two models with two retentions, written to three collections.</b> The diagnostics - the
/// request, its steps and their model calls - go to <c>_traces</c> and <c>_traceSteps</c> and are
/// prunable. The change journal goes to <c>_dataChanges</c> and a purge never touches it.
/// <see cref="TraceCollections"/> is where that split is written down and
/// <see cref="RetentionClass"/> is where the reasoning for it is.
///
/// <b>No new storage mechanism, again.</b> A system collection is an ordinary collection and
/// <c>SystemDocumentStore</c> writes its documents through the same pages, journal and
/// transaction as user data - which is the whole of TR-4: a <see cref="DataChange"/> recorded
/// inside a unit of work commits with the mutation it describes, because it is the same
/// transaction, not because anything here arranges it. The shapes are declared through
/// <c>DescribeSystemCollection</c> so the catalogue says what they hold.
///
/// <b>What is not here yet.</b> Step 3.3 owns the durability rules: reconciling steps left
/// <see cref="StepStatus.Running"/> by a crash, and the bounded delay within which a diagnostic
/// step is on disk. This writes each one as it arrives, which satisfies the stronger half of that
/// and is the thing 3.3 will relax rather than tighten.
/// </summary>
internal sealed class EngineTraces : ITraceRecorder
{
    private const string IdField = "id";
    private const string ConversationField = "conversation";
    private const string OperationField = "operation";
    private const string StateField = "state";
    private const string StartedField = "startedAt";
    private const string EndedField = "endedAt";
    private const string ReasonField = "reason";
    private const string TransitionsField = "transitions";

    private const string RequestField = "request";
    private const string NameField = "name";
    private const string StatusField = "status";
    private const string AfterField = "after";
    private const string InputField = "input";
    private const string OutputField = "output";
    private const string CallField = "call";

    private const string ModelField = "model";
    private const string PromptTokensField = "promptTokens";
    private const string CompletionTokensField = "completionTokens";
    private const string DurationField = "duration";
    private const string PromptHashField = "promptHash";
    private const string RoundTripsField = "roundTrips";
    private const string RetriesField = "retries";
    private const string PeakContextField = "peakContext";

    private const string StepField = "step";
    private const string KindField = "kind";
    private const string CollectionField = "collection";
    private const string AtField = "at";
    private const string ReversibilityField = "reversibility";
    private const string RecordField = "record";
    private const string ContentHashField = "contentHash";
    private const string DispositionField = "disposition";
    private const string FieldsField = "fields";
    private const string OmittedField = "omitted";

    private const string BeforeField = "before";
    private const string ValueField = "value";
    private const string LengthField = "length";
    private const string HashField = "hash";
    private const string PreviewField = "preview";

    private readonly EngineConnection _connection;
    private bool _described;

    public EngineTraces(EngineConnection connection)
    {
        _connection = connection;
    }

    // ---- The request --------------------------------------------------------------------------

    public RequestTrace Begin(Ulid conversationId, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        Describe();

        var request = new RequestTrace(
            Ulid.NewUlid(),
            conversationId,
            operation,
            RequestState.Running,
            DateTimeOffset.UtcNow);

        _connection.InTransaction(() => Write(request));

        return request;
    }

    /// <summary>
    /// A transition as a compare-and-swap on the counter (AG-8a).
    ///
    /// The counter rather than the state, because two answers to the same confirmation both move
    /// it from <see cref="RequestState.WaitingForUser"/> and a check on the state alone would let
    /// the second one through. Step 3.4 puts the single-writer lock around this; what is here is
    /// the shape the lock protects.
    /// </summary>
    public RequestTrace? Move(Ulid requestId, RequestState to, int expectedTransitions, string? reason = null)
    {
        Describe();

        RequestTrace? moved = null;

        _connection.InTransaction(() =>
        {
            if (Request(requestId) is not { } request) return;
            if (request.Transitions != expectedTransitions) return;

            var finished = to is RequestState.Completed or RequestState.Cancelled or RequestState.Failed;

            moved = request with
            {
                State = to,
                Reason = reason ?? request.Reason,
                EndedAt = finished ? DateTimeOffset.UtcNow : null,
                Transitions = request.Transitions + 1
            };

            Write(moved);
        });

        return moved;
    }

    // ---- The steps and the changes --------------------------------------------------------------

    public void Record(ExecutionStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        Describe();

        // Written under its own identity, so the same step recorded again when it finishes
        // replaces the one that said it was running rather than adding a second block to the
        // diagram.
        _connection.InTransaction(() =>
            _connection.SystemDocuments.Write(SystemCollections.TraceSteps, step.Id, ToDocument(step)));
    }

    public void Record(DataChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Describe();

        // TR-4. A unit of work inside one joins it, so a change recorded inside the transaction
        // of the mutation commits with it and neither can exist without the other.
        _connection.InTransaction(() =>
            _connection.SystemDocuments.Write(SystemCollections.DataChanges, change.Id, ToDocument(change)));
    }

    public (RequestTrace Request, IReadOnlyList<ExecutionStep> Steps)? Read(Ulid requestId)
    {
        Describe();

        if (Request(requestId) is not { } request) return null;

        return (request, Steps(requestId));
    }

    public IReadOnlyList<DataChange> Changes(Ulid requestId)
    {
        Describe();

        return [.. _connection.SystemDocuments
            .ReadAll(SystemCollections.DataChanges)
            .Select(static entry => ToChange(entry.Id, entry.Document))
            .Where(change => change.RequestId == requestId)
            .OrderBy(static change => change.Id)];
    }

    // ---- Retention ------------------------------------------------------------------------------

    public int PurgeDiagnostics(DateTimeOffset moment)
    {
        Describe();

        // A request that has not finished is not old, however long ago it started: one waiting
        // for an answer is waiting for a person, and a person takes as long as they take.
        var purged = _connection.SystemDocuments
            .ReadAll(SystemCollections.Traces)
            .Select(static entry => ToRequest(entry.Id, entry.Document))
            .Where(request => request.IsFinished && request.EndedAt is { } ended && ended < moment)
            .Select(static request => request.Id)
            .ToHashSet();

        if (purged.Count == 0) return 0;

        var steps = _connection.SystemDocuments
            .ReadAll(SystemCollections.TraceSteps)
            .Select(static entry => (entry.Id, Request: DocumentFields.Identity(DocumentFields.Of(entry.Document), RequestField)))
            .Where(step => step.Request is { } request && purged.Contains(request))
            .Select(static step => step.Id)
            .ToList();

        _connection.InTransaction(() =>
        {
            foreach (var step in steps)
            {
                _connection.SystemDocuments.Delete(SystemCollections.TraceSteps, step);
            }

            foreach (var request in purged)
            {
                _connection.SystemDocuments.Delete(SystemCollections.Traces, request);
            }
        });

        // _dataChanges is not in this method, and that is the requirement rather than an
        // omission (TR-8). Their request identifiers now dangle, which is what TR-2 asks the
        // interface to render as "the diagram for this change is no longer kept".
        return purged.Count;
    }

    // ---- The documents --------------------------------------------------------------------------

    private void Describe()
    {
        if (_described) return;

        _connection.DescribeSystemCollection(SystemCollections.Traces, RequestColumns());
        _connection.DescribeSystemCollection(SystemCollections.TraceSteps, StepColumns());
        _connection.DescribeSystemCollection(SystemCollections.DataChanges, ChangeColumns());

        _described = true;
    }

    private static List<EngineColumn> RequestColumns() =>
    [
        new(IdField, ValueTypeEnum.Ulid, "The request", unique: true, readOnly: true),
        new(ConversationField, ValueTypeEnum.Ulid, "The conversation it belongs to"),
        new(OperationField, ValueTypeEnum.String, "What was asked for"),
        new(StateField, ValueTypeEnum.String, "Where it has got to (D-15)"),
        new(StartedField, ValueTypeEnum.DateTime, "When it started"),
        new(EndedField, ValueTypeEnum.DateTime, "When it finished, if it has"),
        new(ReasonField, ValueTypeEnum.String, "Why it stopped, where that needs saying"),
        new(TransitionsField, ValueTypeEnum.Int, "How many times it has changed state (AG-8a)")
    ];

    private static List<EngineColumn> StepColumns() =>
    [
        new(IdField, ValueTypeEnum.Ulid, "The step", unique: true, readOnly: true),
        new(RequestField, ValueTypeEnum.Ulid, "The request it belongs to"),
        new(NameField, ValueTypeEnum.String, "What was done"),
        new(StatusField, ValueTypeEnum.String, "How it ended, or that it did not (TR-4b)"),
        new(StartedField, ValueTypeEnum.DateTime, "When it started"),
        new(EndedField, ValueTypeEnum.DateTime, "When it ended, if it did"),
        new(AfterField, ValueTypeEnum.Ulid, "The step it followed: the edges of the diagram"),
        new(InputField, ValueTypeEnum.String, "What it was given"),
        new(OutputField, ValueTypeEnum.String, "What it produced"),
        new(CallField, ValueTypeEnum.Object, "The model call it was, with a prompt hash and never a prompt (TR-3)")
    ];

    private static List<EngineColumn> ChangeColumns() =>
    [
        new(IdField, ValueTypeEnum.Ulid, "The change", unique: true, readOnly: true),
        new(RequestField, ValueTypeEnum.Ulid, "The request that made it: the join that outlives the diagnostics"),
        new(StepField, ValueTypeEnum.Ulid, "The step that made it, while the diagram is still kept"),
        new(KindField, ValueTypeEnum.String, "What it did, which decides what is recorded (TR-2b)"),
        new(CollectionField, ValueTypeEnum.String, "What it changed"),
        new(AtField, ValueTypeEnum.DateTime, "When"),
        new(ReversibilityField, ValueTypeEnum.String, "Whether it can be taken back (D-17)"),
        new(RecordField, ValueTypeEnum.Ulid, "The record, where it was one record"),
        new(ContentHashField, ValueTypeEnum.String, "The record as written, so 'touched since' stays answerable"),
        new(DispositionField, ValueTypeEnum.String, "What happened to the row it came from, on an import (IN-8a)"),
        new(FieldsField, ValueTypeEnum.Array, "What changed, old beside new (TR-2a)"),
        new(OmittedField, ValueTypeEnum.Int, "How many fields the cap left out, where it had to (TR-2b)")
    ];

    private void Write(RequestTrace request)
    {
        var document = new ObjectDocument();
        document.SetIdentifierValue(new UlidDocumentValue(request.Id));
        document.SetValue(new ObjectDocumentValue(new Dictionary<string, IDocumentValue>
        {
            [IdField] = new UlidDocumentValue(request.Id),
            [ConversationField] = new UlidDocumentValue(request.ConversationId),
            [OperationField] = new StringDocumentValue(request.Operation),
            [StateField] = DocumentFields.Word(request.State),
            [StartedField] = new DateTimeDocumentValue(request.StartedAt.UtcDateTime),
            [EndedField] = DocumentFields.Value(request.EndedAt),
            [ReasonField] = DocumentFields.Value(request.Reason),
            [TransitionsField] = new IntDocumentValue(request.Transitions)
        }));

        _connection.SystemDocuments.Write(SystemCollections.Traces, request.Id, document);
    }

    private static ObjectDocument ToDocument(ExecutionStep step)
    {
        var document = new ObjectDocument();
        document.SetIdentifierValue(new UlidDocumentValue(step.Id));
        document.SetValue(new ObjectDocumentValue(new Dictionary<string, IDocumentValue>
        {
            [IdField] = new UlidDocumentValue(step.Id),
            [RequestField] = new UlidDocumentValue(step.RequestId),
            [NameField] = new StringDocumentValue(step.Name),
            [StatusField] = DocumentFields.Word(step.Status),
            [StartedField] = new DateTimeDocumentValue(step.StartedAt.UtcDateTime),
            [EndedField] = DocumentFields.Value(step.EndedAt),
            [AfterField] = DocumentFields.Value(step.After),
            [InputField] = DocumentFields.Value(step.Input),
            [OutputField] = DocumentFields.Value(step.Output),
            [CallField] = step.Call is { } call ? ToValue(call) : new NullDocumentValue()
        }));

        return document;
    }

    private static ObjectDocumentValue ToValue(ModelCall call) =>
        new(new Dictionary<string, IDocumentValue>
        {
            [ModelField] = new StringDocumentValue(call.Model),
            [PromptTokensField] = new IntDocumentValue(call.PromptTokens),
            [CompletionTokensField] = new IntDocumentValue(call.CompletionTokens),
            [DurationField] = new LongDocumentValue(call.Duration.Ticks),
            [PromptHashField] = new StringDocumentValue(call.PromptHash),
            [RoundTripsField] = new IntDocumentValue(call.RoundTrips),
            [RetriesField] = new IntDocumentValue(call.Retries),
            [PeakContextField] = new IntDocumentValue(call.PeakContextTokens)
        });

    private static ObjectDocument ToDocument(DataChange change)
    {
        var document = new ObjectDocument();
        document.SetIdentifierValue(new UlidDocumentValue(change.Id));
        document.SetValue(new ObjectDocumentValue(new Dictionary<string, IDocumentValue>
        {
            [IdField] = new UlidDocumentValue(change.Id),
            [RequestField] = new UlidDocumentValue(change.RequestId),
            [StepField] = DocumentFields.Value(change.StepId),
            [KindField] = DocumentFields.Word(change.Kind),
            [CollectionField] = new StringDocumentValue(change.CollectionName),
            [AtField] = new DateTimeDocumentValue(change.At.UtcDateTime),
            [ReversibilityField] = DocumentFields.Word(change.Reversibility),
            [RecordField] = DocumentFields.Value(change.RecordId),
            [ContentHashField] = DocumentFields.Value(change.ContentHash),
            [DispositionField] = DocumentFields.Value(change.Disposition),
            [FieldsField] = new ArrayDocumentValue([.. change.Fields.Select(ToValue)]),
            [OmittedField] = new IntDocumentValue(change.OmittedFields)
        }));

        return document;
    }

    private static IDocumentValue ToValue(FieldChange field) =>
        new ObjectDocumentValue(new Dictionary<string, IDocumentValue>
        {
            [NameField] = new StringDocumentValue(field.Name),
            [BeforeField] = ToValue(field.Before),
            [AfterField] = ToValue(field.After)
        });

    private static IDocumentValue ToValue(JournalValue value) =>
        new ObjectDocumentValue(new Dictionary<string, IDocumentValue>
        {
            [KindField] = DocumentFields.Word(value.Kind),
            [ValueField] = Stored(value.Value),
            [LengthField] = new IntDocumentValue(value.Length),
            [HashField] = DocumentFields.Value(value.Hash),
            [PreviewField] = DocumentFields.Value(value.Preview)
        });

    /// <summary>
    /// A journalled value as a document value. The two the document format has no word for are
    /// the two the storage contract does: a day and a moment both go in as a moment, and the
    /// kind beside them is what brings them back as what they were.
    /// </summary>
    private static IDocumentValue Stored(object? value) => value switch
    {
        null => new NullDocumentValue(),
        DateOnly day => new DateTimeDocumentValue(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)),
        DateTimeOffset moment => new DateTimeDocumentValue(moment.UtcDateTime),
        Ulid identity => new UlidDocumentValue(identity),
        _ => DocumentValues.From(value)
    };

    private static object? Restored(JournalKind kind, IDocumentValue stored) => stored switch
    {
        NullDocumentValue => null,
        DateTimeDocumentValue moment when kind is JournalKind.Date =>
            DateOnly.FromDateTime(moment.Value),
        DateTimeDocumentValue moment =>
            DateTime.SpecifyKind(moment.Value, DateTimeKind.Utc),
        StringDocumentValue text => text.Value,
        IntDocumentValue number => kind is JournalKind.Integer ? (long)number.Value : number.Value,
        LongDocumentValue number => number.Value,
        DecimalDocumentValue number => number.Value,
        BooleanDocumentValue flag => flag.Value,
        UlidDocumentValue identity => identity.Value,
        GuidDocumentValue identity => identity.Value,
        _ => null
    };

    // ---- Back again -----------------------------------------------------------------------------

    private RequestTrace? Request(Ulid requestId)
    {
        foreach (var (id, document) in _connection.SystemDocuments.ReadAll(SystemCollections.Traces))
        {
            if (id == requestId) return ToRequest(id, document);
        }

        return null;
    }

    private IReadOnlyList<ExecutionStep> Steps(Ulid requestId) =>
        [.. _connection.SystemDocuments
            .ReadAll(SystemCollections.TraceSteps)
            .Select(static entry => ToStep(entry.Id, entry.Document))
            .Where(step => step.RequestId == requestId)
            .OrderBy(static step => step.StartedAt)
            .ThenBy(static step => step.Id)];

    private static RequestTrace ToRequest(Ulid id, ObjectDocument document)
    {
        var value = DocumentFields.Of(document);

        return new RequestTrace(
            id,
            DocumentFields.Identity(value, ConversationField) ?? default,
            DocumentFields.Text(value, OperationField),
            DocumentFields.Word(value, StateField, RequestState.Running),
            DocumentFields.Moment(value, StartedField),
            DocumentFields.OptionalMoment(value, EndedField),
            DocumentFields.OptionalText(value, ReasonField))
        {
            Transitions = DocumentFields.Number(value, TransitionsField)
        };
    }

    private static ExecutionStep ToStep(Ulid id, ObjectDocument document)
    {
        var value = DocumentFields.Of(document);

        return new ExecutionStep(
            id,
            DocumentFields.Identity(value, RequestField) ?? default,
            DocumentFields.Text(value, NameField),
            DocumentFields.Word(value, StatusField, StepStatus.Interrupted),
            DocumentFields.Moment(value, StartedField),
            DocumentFields.OptionalMoment(value, EndedField))
        {
            After = DocumentFields.Identity(value, AfterField),
            Input = DocumentFields.OptionalText(value, InputField),
            Output = DocumentFields.OptionalText(value, OutputField),
            Call = value.Values.GetValueOrDefault(CallField) is ObjectDocumentValue call ? ToCall(call) : null
        };
    }

    private static ModelCall ToCall(ObjectDocumentValue value) =>
        new(
            DocumentFields.Text(value, ModelField),
            DocumentFields.Number(value, PromptTokensField),
            DocumentFields.Number(value, CompletionTokensField),
            TimeSpan.FromTicks(DocumentFields.Ticks(value, DurationField)),
            DocumentFields.Text(value, PromptHashField))
        {
            RoundTrips = DocumentFields.Number(value, RoundTripsField),
            Retries = DocumentFields.Number(value, RetriesField),
            PeakContextTokens = DocumentFields.Number(value, PeakContextField)
        };

    private static DataChange ToChange(Ulid id, ObjectDocument document)
    {
        var value = DocumentFields.Of(document);

        return new DataChange(
            id,
            DocumentFields.Identity(value, RequestField) ?? default,
            DocumentFields.Word(value, KindField, ChangeKind.Update),
            DocumentFields.Text(value, CollectionField),
            DocumentFields.Moment(value, AtField),
            DocumentFields.Word(value, ReversibilityField, Reversibility.NotReversible))
        {
            RecordId = DocumentFields.Identity(value, RecordField),
            StepId = DocumentFields.Identity(value, StepField),
            ContentHash = DocumentFields.OptionalText(value, ContentHashField),
            Disposition = DocumentFields.OptionalText(value, DispositionField),
            Fields = value.Values.GetValueOrDefault(FieldsField) is ArrayDocumentValue fields
                ? [.. fields.Values.OfType<ObjectDocumentValue>().Select(ToFieldChange)]
                : [],
            OmittedFields = DocumentFields.Number(value, OmittedField)
        };
    }

    private static FieldChange ToFieldChange(ObjectDocumentValue value) =>
        new(
            DocumentFields.Text(value, NameField),
            ToJournalValue(value.Values.GetValueOrDefault(BeforeField)),
            ToJournalValue(value.Values.GetValueOrDefault(AfterField)));

    private static JournalValue ToJournalValue(IDocumentValue? stored)
    {
        if (stored is not ObjectDocumentValue value) return JournalValue.Nothing;

        var kind = DocumentFields.Word(value, KindField, JournalKind.Unknown);

        return new JournalValue(
            kind,
            Restored(kind, value.Values.GetValueOrDefault(ValueField) ?? new NullDocumentValue()),
            DocumentFields.Number(value, LengthField),
            DocumentFields.OptionalText(value, HashField),
            DocumentFields.OptionalText(value, PreviewField));
    }
}
