using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// One record that blocks an undo, and why (AG-11b): touched since, a version no longer kept,
/// or a conflict in the state the undo would end in.
/// </summary>
internal sealed record Blocker(RecordReference Record, string Reason, RecordReference? Other = null)
{
    public override string ToString() => Other is { } other ? $"{Record}: {Reason} ({other})" : $"{Record}: {Reason}";
}

/// <summary>
/// The undo was refused whole (AG-11b): before anything was written, naming every blocker
/// the validation found - or at replay, naming the one record a constraint refused, with
/// storage left as it was.
/// </summary>
internal sealed class CompensationRefusedException(IReadOnlyList<Blocker> blockers, bool atReplay)
    : Exception($"The undo was refused {(atReplay ? "at replay" : "before anything was written")}: " +
                string.Join("; ", blockers))
{
    public IReadOnlyList<Blocker> Blockers { get; } = blockers;
    public bool AtReplay { get; } = atReplay;
}

/// <summary>
/// Compensation of one request's record changes, as AJ-4 of the versioning plan states it and
/// as step 4.7 of the assistant plan will adopt it: two passes inside one unit of work, over
/// <see cref="IStorage"/> and nothing else, so that it means the same over both backends.
///
/// <b>Test-local because <c>TokkDb.Assistant.Agents</c> does not exist yet</b>; step 4.7 lifts
/// it unchanged. Structural changes are not here - their inverse is in the journal's payload
/// (AG-11e) and is replayed at its position by the same step.
/// </summary>
internal static class Compensation
{
    /// <summary>AJ-5: a record change's reversibility, from what its collection declares, before it runs.</summary>
    public static Reversibility ReversibilityOf(IStorage storage, string collectionName)
    {
        var definition = storage.GetCollectionDefinition(collectionName)
            ?? throw new UnknownCollectionException(collectionName);

        var unique = definition.Columns.Any(static column => column.Unique);
        var related = storage.GetRelations().Any(relation =>
            StorageNames.Same(relation.FromCollection, definition.Name) || StorageNames.Same(relation.ToCollection, definition.Name));

        return Reversibilities.OfRecordChange(unique, related);
    }

    /// <summary>
    /// Undoes the request's record changes - all of them, or the subset <paramref name="include"/>
    /// selects, record by record - validating the whole of it first and writing nothing unless
    /// nothing blocks. A record left out counts as unchanged by the request.
    /// </summary>
    public static IReadOnlyList<DataChange> Undo(IStorage storage, IReadOnlyList<DataChange> changes, Func<DataChange, bool>? include = null)
    {
        var included = changes.Where(change => change.IsRecordChange && (include?.Invoke(change) ?? true)).ToList();

        var blockers = Validate(storage, included);
        if (blockers.Count > 0) throw new CompensationRefusedException(blockers, atReplay: false);

        storage.InUnitOfWork(() => Replay(storage, included));

        return included;
    }

    /// <summary>
    /// The validation pass (AG-11a): every blocker, found before anything is written, over any
    /// subset of a request's changes.
    /// </summary>
    public static IReadOnlyList<Blocker> Validate(IStorage storage, IReadOnlyList<DataChange> included)
    {
        var blockers = new List<Blocker>();
        var byRecord = included
            .Where(static change => change.RecordId is not null)
            .GroupBy(static change => new RecordReference(change.CollectionName, change.RecordId!.Value))
            .ToDictionary(static group => group.Key, static group => group.ToList());

        // Pass 1a: touched since, and versions kept. The end state of each record: the version
        // its earliest change replaced, or nothing for a record the request inserted.
        var endStates = new Dictionary<RecordReference, EndState>();

        foreach (var (record, changes) in byRecord)
        {
            var last = changes[^1];
            var head = storage.HeadVersion(record.CollectionName, record.Id);

            if (last.VersionId is null)
            {
                blockers.Add(new Blocker(record, "written before version references existed, so it cannot be undone from history"));
                continue;
            }

            if (head != last.VersionId)
            {
                blockers.Add(new Blocker(record, $"touched since: its head is {head?.ToString() ?? "gone"}, not {last.VersionId}"));
                continue;
            }

            var target = changes[0].PreviousVersionId;
            if (target is null)
            {
                endStates[record] = EndState.Gone;
                continue;
            }

            if (!storage.Keeps(record.CollectionName, record.Id, target.Value))
            {
                blockers.Add(new Blocker(record, $"not reversible: version {target} is no longer kept"));
                continue;
            }

            // The end state from the current values and the diff to the target (AG-11a).
            var current = storage.GetById(record.CollectionName, record.Id);
            var fields = new Dictionary<string, object?>(current?.Fields ?? new Dictionary<string, object?>(), StorageNames.Comparer);
            foreach (var column in storage.DiffVersions(record.CollectionName, record.Id, last.VersionId.Value, target.Value).Changes)
            {
                fields[column.ColumnName] = column.After;
            }

            endStates[record] = new EndState(false, fields);
        }

        // Pass 1b: the end state against everything the request did not change.
        var relations = storage.GetRelations();

        foreach (var (record, end) in endStates)
        {
            var definition = storage.GetCollectionDefinition(record.CollectionName)!;

            if (end.Deleted)
            {
                // Still referred to from outside once it is gone (the restrict case of V-18).
                var current = storage.GetById(record.CollectionName, record.Id);
                if (current is null) continue;

                foreach (var relation in relations.Where(relation => StorageNames.Same(relation.ToCollection, definition.Name)))
                {
                    var key = current[relation.ToColumn];
                    if (key is null) continue;

                    foreach (var referrer in Referrers(storage, relation, key))
                    {
                        var reference = new RecordReference(relation.FromCollection, referrer.Id);
                        if (endStates.TryGetValue(reference, out var referrerEnd)
                            && (referrerEnd.Deleted || !SameValue(storage, relation.FromCollection, relation.FromColumn, referrerEnd.Fields.GetValueOrDefault(relation.FromColumn), key)))
                        {
                            continue;
                        }

                        blockers.Add(new Blocker(record, $"would be deleted while '{relation.Name}' still refers to it from {reference}", reference));
                    }
                }

                continue;
            }

            // Unique collisions with records the request did not change.
            foreach (var column in definition.Columns.Where(static column => column.Unique))
            {
                if (end.Fields.GetValueOrDefault(column.Name) is not { } value) continue;

                var holder = storage.MatchValues(definition.Name, column.Name, [value]).Holder(value);
                if (holder is null || holder == record.Id) continue;

                var reference = new RecordReference(definition.Name, holder.Value);
                if (endStates.TryGetValue(reference, out var holderEnd)
                    && (holderEnd.Deleted || !SameValue(storage, definition.Name, column.Name, holderEnd.Fields.GetValueOrDefault(column.Name), value)))
                {
                    // Changed by the request too, and not holding the value once it is undone.
                    continue;
                }

                blockers.Add(new Blocker(record, $"'{column.Name}' would be {ColumnTypes.Render(value)}, which {reference} holds", reference));
            }

            // References that would not resolve.
            foreach (var relation in relations.Where(relation => StorageNames.Same(relation.FromCollection, definition.Name)))
            {
                if (end.Fields.GetValueOrDefault(relation.FromColumn) is not { } value) continue;

                var holder = storage.MatchValues(relation.ToCollection, relation.ToColumn, [value]).Holder(value);
                var reference = holder is { } id ? new RecordReference(relation.ToCollection, id) : (RecordReference?)null;

                // A record the undo restores may be the holder once it is back, whether or not it is there now.
                var restoredHolder = endStates.Any(entry =>
                    StorageNames.Same(entry.Key.CollectionName, relation.ToCollection) && !entry.Value.Deleted
                    && SameValue(storage, relation.ToCollection, relation.ToColumn, entry.Value.Fields.GetValueOrDefault(relation.ToColumn), value));

                if (reference is { } target && endStates.TryGetValue(target, out var targetEnd)
                    && (targetEnd.Deleted || !SameValue(storage, relation.ToCollection, relation.ToColumn, targetEnd.Fields.GetValueOrDefault(relation.ToColumn), value))
                    && !restoredHolder)
                {
                    blockers.Add(new Blocker(record, $"'{relation.FromColumn}' would refer to {ColumnTypes.Render(value)}, which the undo takes away from {target}", target));
                }
                else if (reference is null && !restoredHolder)
                {
                    blockers.Add(new Blocker(record, $"'{relation.FromColumn}' would refer to {ColumnTypes.Render(value)}, which nothing in '{relation.ToCollection}' holds"));
                }
            }
        }

        return blockers;
    }

    /// <summary>
    /// The replay pass (AG-11f): every change in exact reverse order, each restoring the version
    /// it replaced - or deleting the record, for an insert. Nothing the compensation itself
    /// writes is re-checked; a constraint refusal aborts the whole unit of work, naming the record.
    /// </summary>
    private static void Replay(IStorage storage, IReadOnlyList<DataChange> included)
    {
        foreach (var change in included.Reverse())
        {
            var record = new RecordReference(change.CollectionName, change.RecordId!.Value);

            try
            {
                if (change.PreviousVersionId is null)
                {
                    storage.Delete(change.CollectionName, record.Id);
                }
                else
                {
                    storage.RestoreVersion(change.CollectionName, record.Id, change.PreviousVersionId.Value);
                }
            }
            catch (StorageValidationException refused)
            {
                var other = refused.Errors.OfType<DuplicateValue>().Select(error => new RecordReference(error.CollectionName, error.HeldBy)).FirstOrDefault();
                throw new CompensationRefusedException(
                    [new Blocker(record, string.Join("; ", refused.Errors.Select(static error => error.Describe())), other)],
                    atReplay: true);
            }
            catch (IntegrityRefusedException refused)
            {
                throw new CompensationRefusedException(
                    [new Blocker(record, refused.Message, refused.Referring.Count > 0 ? refused.Referring[0] : null)], atReplay: true);
            }
        }
    }

    private static IEnumerable<StorageRecord> Referrers(IStorage storage, RelationDefinition relation, object key) =>
        storage.GetAll(relation.FromCollection)
            .Where(record => record[relation.FromColumn] is { } value && SameValue(storage, relation.FromCollection, relation.FromColumn, value, key));

    /// <summary>Compared the way the index that enforces the rule compares: text folded.</summary>
    private static bool SameValue(IStorage storage, string collectionName, string columnName, object? left, object? right)
    {
        if (left is null || right is null) return false;

        var type = storage.GetCollectionDefinition(collectionName)?.Column(columnName)?.Type ?? ColumnType.Text;
        return Equals(TextComparison.AsCompared(type, left), TextComparison.AsCompared(type, right));
    }

    private sealed record EndState(bool Deleted, IReadOnlyDictionary<string, object?> Fields)
    {
        public static readonly EndState Gone = new(true, new Dictionary<string, object?>());
    }
}
