using TokkDb.Assistant.Agents.Changes;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Agents.Orchestration;

/// <summary>
/// Writes the change records around every write (TR-2b, TR-4, AJ-5): a record change with the
/// versions it replaced and produced and the reversibility its collection declares, a structural
/// change with its bounded inverse. Called inside the unit of work that makes the change, so that
/// neither can exist without the other.
/// </summary>
public sealed class ChangeJournal
{
    private readonly IStorage _storage;
    private readonly ITraceRecorder _recorder;
    private readonly ChangeClassifier _classifier;
    private readonly PayloadLimits _limits;

    public ChangeJournal(IStorage storage, ITraceRecorder recorder, ChangeClassifier classifier, PayloadLimits? limits = null)
    {
        _storage = storage;
        _recorder = recorder;
        _classifier = classifier;
        _limits = limits ?? PayloadLimits.Default;
    }

    public DataChange Inserted(Ulid request, string thing, Ulid record, Ulid? step, string? disposition = null)
    {
        var change = DataChanges.Insert(request, thing, record, Head(thing, record), _classifier.RecordReversibility(thing)) with
        {
            StepId = step,
            Disposition = disposition
        };
        _recorder.Record(change);
        return change;
    }

    public DataChange Updated(Ulid request, string thing, Ulid record, Ulid previousVersion, Ulid? step, string? disposition = null)
    {
        var change = DataChanges.Update(request, thing, record, previousVersion, Head(thing, record), _classifier.RecordReversibility(thing)) with
        {
            StepId = step,
            Disposition = disposition
        };
        _recorder.Record(change);
        return change;
    }

    public DataChange Deleted(Ulid request, string thing, Ulid record, Ulid previousVersion, Ulid? step)
    {
        var change = DataChanges.Delete(request, thing, record, previousVersion, Head(thing, record), _classifier.RecordReversibility(thing)) with
        {
            StepId = step
        };
        _recorder.Record(change);
        return change;
    }

    public DataChange Structural(Ulid request, ChangeKind kind, string thing, IReadOnlyList<FieldChange> fields, Reversibility reversibility, Ulid? step)
    {
        var change = DataChanges.Structural(request, kind, thing, fields, reversibility, _limits) with { StepId = step };
        _recorder.Record(change);
        return change;
    }

    /// <summary>The removed values of a field, as the journal keeps them (AG-11e): one entry per record, keyed by the record.</summary>
    public IReadOnlyList<FieldChange> RemovedValues(string thing, string field) =>
        [.. _storage.GetAll(thing)
            .Where(record => record[field] is not null)
            .OrderBy(static record => record.Id)
            .Select(record => FieldChange.Removed(record.Id.ToString(), JournalValue.Of(record[field], _limits)))];

    private Ulid Head(string thing, Ulid record) =>
        _storage.HeadVersion(thing, record) ?? throw new InvalidOperationException($"Record {record} of '{thing}' has no version to record.");
}
