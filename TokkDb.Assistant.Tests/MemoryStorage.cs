using System.Diagnostics;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// <see cref="IStorage"/> in dictionaries.
///
/// It is not a stub. It obeys the contract, including the parts that are easy to skip - a
/// refused write leaves nothing behind, a failed unit of work leaves nothing behind, a value
/// comes back as the type it went in as, and a value that will not convert is kept and flagged
/// rather than quietly dropped - because a fake that is easier to satisfy than the real thing
/// makes the contract suite worth less than the time it takes to run.
///
/// The judgement is not reimplemented here. <c>SharedRules</c> in the contract assembly decides
/// what fits and <c>RecordIdentity</c> issues identities, so the only things this file decides
/// are the ones a storage layout genuinely decides: how to find the holder of a value, and what
/// order records come back in.
///
/// <b>It shuffles.</b> <see cref="GetAll"/> deliberately does not return records in the order
/// they were created. SC-4 settled that the contract promises no order, and a fake that happened
/// to return insertion order would let a caller depend on it here and fail against the engine
/// later. Shuffling turns that into a failure now, which is the only time it is cheap. The
/// shuffle is seeded per instance, so a failure is reproducible.
///
/// <b>And it reports a scan, because it performs one.</b> SC-7a forbids the tempting alternative:
/// a fake that returned a "deterministic equivalent" of an index seek would make the shared suite
/// pass against a claim no code supports, and the suite exists to catch exactly that.
/// </summary>
public sealed class MemoryStorage : IStorage
{
    private readonly Random _order = new(20260912);
    private readonly MemoryConversations _conversations = new();

    private Dictionary<string, Thing> _things = new(StorageNames.Comparer);
    private Dictionary<string, RelationDefinition> _relations = new(StorageNames.Comparer);

    private Dictionary<string, Thing>? _before;
    private Dictionary<string, RelationDefinition>? _beforeRelations;
    private int _depth;

    public IConversationStore Conversations => _conversations;

    public void InUnitOfWork(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        InUnitOfWork(() =>
        {
            work();
            return true;
        });
    }

    public T InUnitOfWork<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        // The outermost one is the one that can undo anything, so it is the one that remembers
        // what to undo to. An inner unit of work joins it and keeps no mark of its own.
        if (_depth++ == 0)
        {
            _before = Copy(_things);
            _beforeRelations = new Dictionary<string, RelationDefinition>(_relations, StorageNames.Comparer);
        }

        try
        {
            var result = work();

            if (--_depth == 0)
            {
                FlushTouched();
                _before = null;
                _beforeRelations = null;
            }

            return result;
        }
        catch
        {
            if (--_depth == 0)
            {
                _touched.Clear();
                _things = _before!;
                _relations = _beforeRelations!;
                _before = null;
                _beforeRelations = null;
            }

            throw;
        }
    }

    public void CreateCollection(CollectionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        SharedRules.CheckDefinitionFitsItself(definition);

        if (_things.ContainsKey(definition.Name))
        {
            throw new CollectionAlreadyExistsException(definition.Name);
        }

        _things[definition.Name] = new Thing(definition, [], []) { LastChanged = DateTimeOffset.UtcNow };
    }

    public CollectionDefinition? GetCollectionDefinition(string collectionName) =>
        _things.TryGetValue(Name(collectionName), out var thing) ? thing.Definition : null;

    public IReadOnlyCollection<CollectionDefinition> GetCollectionDefinitions() =>
        Shuffled(_things.Values.Select(static thing => thing.Definition));

    public bool DeleteCollection(string collectionName)
    {
        var name = Name(collectionName);

        if (!_things.Remove(name)) return false;

        foreach (var relation in _relations.Values.ToList())
        {
            if (StorageNames.Same(relation.FromCollection, name) || StorageNames.Same(relation.ToCollection, name))
            {
                _relations.Remove(relation.Name);
            }
        }

        return true;
    }

    public void SetMetadata(string collectionName, IReadOnlyDictionary<string, string?> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var thing = Require(collectionName);
        Touch(thing.Definition.Name);
        Replace(thing, thing.Definition.WithMetadata(metadata));
    }

    public void SetDisplayRule(string collectionName, DisplayRule? displayRule)
    {
        var thing = Require(collectionName);
        Touch(thing.Definition.Name);
        var after = thing.Definition.WithDisplayRule(displayRule);

        SharedRules.CheckDefinitionFitsItself(after);
        Replace(thing, after);
    }

    public StorageRecord Create(string collectionName, IReadOnlyDictionary<string, object?> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var thing = Require(collectionName);
        var judged = SharedRules.ForCreate(thing.Definition, fields, thing.HolderOf);

        SharedRules.CheckReferences(thing.Definition, judged, From(thing.Definition.Name), Exists);

        var record = new StorageRecord(RecordIdentity.Next(), thing.Definition.Name, judged);

        thing.Records[record.Id] = record;
        thing.Remember(record.Id, record);
        thing.LastChanged = DateTimeOffset.UtcNow;
        return record;
    }

    public StorageRecord? GetById(string collectionName, Ulid id) =>
        Require(collectionName).Records.GetValueOrDefault(id);

    public bool Update(StorageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var thing = Require(record.CollectionName);

        if (!thing.Records.TryGetValue(record.Id, out var existing))
        {
            return false;
        }

        var judged = SharedRules.ForUpdate(thing.Definition, record, existing, thing.HolderOf);

        SharedRules.CheckReferences(thing.Definition, judged, From(thing.Definition.Name), Exists);

        // A value that is still not of its column's type is still not of it after a write that
        // handed it back unchanged (SC-6b), so the flag survives with it.
        var attention = existing.NeedsAttention
            .Where(column => judged.TryGetValue(column, out var value) && Equals(value, existing[column]))
            .ToArray();

        var written = new StorageRecord(record.Id, thing.Definition.Name, judged, attention);
        thing.Records[record.Id] = written;
        thing.Remember(record.Id, written);
        thing.LastChanged = DateTimeOffset.UtcNow;
        return true;
    }

    public DeletionResult Delete(string collectionName, Ulid id)
    {
        var thing = Require(collectionName);
        var effect = InspectDelete(thing, id);

        if (!effect.RecordExists) return DeletionResult.NotFound;

        if (effect.Blocking.Count > 0)
        {
            throw new IntegrityRefusedException(
                Refusing(thing, id)!, new RecordReference(thing.Definition.Name, id), effect.Blocking);
        }

        var removed = new List<RecordReference>();
        var cleared = new List<RecordReference>();

        InUnitOfWork(() =>
        {
            foreach (var reference in effect.WouldAlsoBeRemoved)
            {
                if (Require(reference.CollectionName).Forget(reference.Id)) removed.Add(reference);
            }

            foreach (var reference in effect.WouldBeCleared)
            {
                if (Clear(reference, thing.Definition.Name)) cleared.Add(reference);
            }

            thing.Forget(id);
        });

        return new DeletionResult(true, removed, cleared);
    }

    public DeletionEffect InspectDelete(string collectionName, Ulid id) =>
        InspectDelete(Require(collectionName), id);

    public IReadOnlyCollection<StorageRecord> GetAll(string collectionName) =>
        Shuffled(Require(collectionName).Records.Values);

    /// <summary>
    /// SC-7 without a planner. There is no index here and there is no honest way to report one:
    /// a dictionary of records answers every condition by looking at every record, so a query
    /// says so - except a lookup by identity, which really is one, because the records are held
    /// by identity.
    /// </summary>
    public StorageQueryResult ExecuteQuery(StorageQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var thing = Require(query.CollectionName);

        if (!QueryTraversals.TryResolve(query, Relation, ExecuteQuery, out var resolved))
        {
            return new StorageQueryResult(
                thing.Definition.Name,
                [],
                Info(QueryAccess.Scan, thing, null, "nothing on the other side of the relation matched", 0, 0, 0,
                    TimeSpan.Zero),
                QueryMatching.Aggregate([], QueryValidation.Against(thing.Definition, query.Resolved([])).Aggregates));
        }

        var validated = QueryValidation.Against(thing.Definition, resolved);

        var started = Stopwatch.GetTimestamp();

        var byIdentity = query.Ids.Count > 0;
        var candidates = byIdentity
            ? query.Ids.Select(thing.Records.GetValueOrDefault).OfType<StorageRecord>().ToArray()
            : Shuffled(thing.Records.Values).ToArray();

        var matched = candidates
            .Where(record => validated.Where.All(condition => QueryMatching.Matches(record, condition)))
            .ToList();

        if (resolved.After is { } cursor && validated.OrderBy.Count > 0)
        {
            matched = [.. matched.Where(record => QueryMatching.After(record, validated.OrderBy, cursor))];
        }

        var aggregates = QueryMatching.Aggregate(matched, validated.Aggregates);

        var page = QueryMatching.Ordered(matched, validated.OrderBy).Skip(query.Skip);
        if (query.Take is { } take) page = page.Take(take + 1);

        var records = page.ToList();

        var more = query.Take is { } wanted && records.Count > wanted;
        if (more) records.RemoveAt(records.Count - 1);

        var next = more && validated.OrderBy.Count > 0
            ? QueryMatching.CursorFor(records[^1], validated.OrderBy)
            : null;

        var projected = validated.Select.Count == 0
            ? records
            : [.. records.Select(record => QueryMatching.Project(record, validated.Select))];

        return new StorageQueryResult(
            thing.Definition.Name,
            projected,
            Info(
                byIdentity ? QueryAccess.IdentityLookup : QueryAccess.Scan,
                thing,
                null,
                byIdentity
                    ? $"lookup of {query.Ids.Count} records by identity in {thing.Definition.Name}"
                    : $"full scan of {thing.Definition.Name} (nothing here is indexed)",
                candidates.Length,
                projected.Count,
                Excluded(thing, validated),
                Stopwatch.GetElapsedTime(started)),
            aggregates,
            next);
    }

    /// <summary>
    /// SC-9, answered the only way a dictionary can: by looking at every record once. The shape
    /// of the call is what the contract is about - a set in, a set out - and the execution info
    /// says a scan happened, because one did (SC-7a).
    /// </summary>
    public ValueSetResult MatchValues(string collectionName, string columnName, IReadOnlyCollection<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var thing = Require(collectionName);
        var column = thing.Definition.Column(columnName)
            ?? throw new UnknownColumnException(
                thing.Definition.Name, StorageNames.Normalise(columnName, "column name"));

        var started = Stopwatch.GetTimestamp();

        var wanted = new Dictionary<object, object>();
        foreach (var value in values)
        {
            if (value is null) continue;
            if (!ColumnTypes.TryCanonicalise(column.Type, value, out var canonical) || canonical is null) continue;

            wanted.TryAdd(TextComparison.AsCompared(column.Type, canonical)!, canonical);
        }

        var found = new List<ValueMatch>();

        foreach (var record in thing.Records.Values)
        {
            if (record[column.Name] is not { } held) continue;

            var key = TextComparison.AsCompared(column.Type, held)!;

            if (wanted.Remove(key, out var asked)) found.Add(new ValueMatch(asked, record.Id));
        }

        return new ValueSetResult(found, Info(
            QueryAccess.Scan,
            thing,
            column.Name,
            $"one pass over {thing.Definition.Name}, which has no index on anything",
            thing.Records.Count,
            found.Count,
            0,
            Stopwatch.GetElapsedTime(started)));
    }

    public int CountNeedingAttention(string collectionName, string columnName)
    {
        var thing = Require(collectionName);
        var column = thing.Definition.Column(columnName)
            ?? throw new UnknownColumnException(
                thing.Definition.Name, StorageNames.Normalise(columnName, "column name"));

        return thing.Records.Values.Count(record => record.NeedsAttention.Contains(column.Name));
    }

    // ---- Structural change (SC-6) ----------------------------------------------------------
    //
    // There is no lazy migration here and there is nothing to gain from pretending otherwise: a
    // dictionary has no schema version and no records written under an older one. So each change
    // is applied to the records as it is made, and Converge has nothing left to do.
    //
    // What matters is that the two implementations answer the same question the same way, and
    // the question is what a read returns afterwards - not whether the work happened at the
    // change or at the read. StructuralChange decides what the definition becomes and
    // ColumnConversion decides what a value becomes, both shared, so the only thing decided here
    // is when.

    public void AddColumn(string collectionName, ColumnDefinition column)
    {
        var thing = Require(collectionName);
        Touch(thing.Definition.Name);
        Replace(thing, StructuralChange.Add(thing.Definition, column));
    }

    public void RenameColumn(string collectionName, string columnName, string newName)
    {
        var thing = Require(collectionName);
        Touch(thing.Definition.Name);
        var before = thing.Definition;
        var after = StructuralChange.Rename(before, columnName, newName);
        if (ReferenceEquals(before, after)) return;

        var from = StorageNames.Normalise(columnName, "column name");
        var to = StorageNames.Normalise(newName, "column name");

        Rewrite(thing, after, (fields, attention) =>
        {
            // Only a column the record actually had moves. One it never had stays not there,
            // rather than appearing under the new name holding nothing.
            if (fields.Remove(from, out var value)) fields[to] = value;
            if (attention.Remove(from)) attention.Add(to);
        }, renamed: (from, to));
    }

    /// <summary>
    /// SC-6b in the simplest possible storage: a value that will not convert stays exactly where
    /// it is and the column it is in is named as needing attention. Nothing is dropped, nothing
    /// is refused, and the record reads normally in every other column.
    /// </summary>
    public void RetypeColumn(string collectionName, string columnName, ColumnType newType)
    {
        var thing = Require(collectionName);
        Touch(thing.Definition.Name);
        var before = thing.Definition;
        var after = StructuralChange.Retype(before, columnName, newType);
        if (ReferenceEquals(before, after)) return;

        var name = StorageNames.Normalise(columnName, "column name");
        var was = before.Column(name)!.Type;

        Rewrite(thing, after, (fields, attention) =>
        {
            if (!fields.TryGetValue(name, out var value)) return;

            // A value already waiting to be made sense of is converted from what it actually is,
            // not from what the column used to say: a second retype does not make it worse.
            var from = attention.Contains(name) && ColumnTypes.TryRecordedType(value, out var recorded)
                ? recorded
                : was;

            if (ColumnConversion.TryRetype(from, newType, value, out var converted))
            {
                fields[name] = converted;
                attention.Remove(name);
            }
            else
            {
                attention.Add(name);
            }
        }, retypedColumn: name);
    }

    public void RemoveColumn(string collectionName, string columnName)
    {
        var thing = Require(collectionName);
        Touch(thing.Definition.Name);
        var after = StructuralChange.Remove(thing.Definition, columnName);
        var name = StorageNames.Normalise(columnName, "column name");

        Rewrite(thing, after, (fields, attention) =>
        {
            fields.Remove(name);
            attention.Remove(name);
        }, removedColumn: name);
    }

    /// <summary>
    /// Nothing to converge: every change was applied when it was made. What it still has to do is
    /// report what it could not convert, which is the same set the engine reports once it has
    /// caught up - and reporting it twice is the same answer twice, which is what idempotent
    /// means here.
    /// </summary>
    public ConvergeReport Converge(string collectionName)
    {
        var thing = Require(collectionName);

        var stubborn = new List<UnconvertibleValue>();

        foreach (var record in thing.Records.Values)
        {
            foreach (var column in record.NeedsAttention)
            {
                var value = record[column];

                stubborn.Add(new UnconvertibleValue(
                    record.Id,
                    column,
                    value,
                    ColumnTypes.TryRecordedType(value, out var recorded) ? recorded : ColumnType.Text));
            }
        }

        return new ConvergeReport(thing.Definition.Name, 0, stubborn);
    }

    public RetypeEffect InspectRetype(string collectionName, string columnName, ColumnType newType)
    {
        var thing = Require(collectionName);
        var column = thing.Definition.Column(columnName)
            ?? throw new UnknownColumnException(
                thing.Definition.Name, StorageNames.Normalise(columnName, "column name"));

        var inspected = 0;
        var refusing = new List<UnconvertibleValue>();

        foreach (var record in thing.Records.Values)
        {
            if (record[column.Name] is not { } value) continue;

            inspected++;

            var from = record.NeedsAttention.Contains(column.Name)
                       && ColumnTypes.TryRecordedType(value, out var recorded)
                ? recorded
                : column.Type;

            if (!ColumnConversion.TryRetype(from, newType, value, out _))
            {
                refusing.Add(new UnconvertibleValue(record.Id, column.Name, value, from));
            }
        }

        return new RetypeEffect(
            thing.Definition.Name,
            column.Name,
            column.Type,
            newType,
            ColumnConversion.IsLossless(column.Type, newType),
            inspected,
            refusing);
    }

    public UniquenessEffect InspectUnique(string collectionName, string columnName)
    {
        var thing = Require(collectionName);
        var column = thing.Definition.Column(columnName)
            ?? throw new UnknownColumnException(
                thing.Definition.Name, StorageNames.Normalise(columnName, "column name"));

        var byValue = new Dictionary<object, List<RecordReference>>();
        var inspected = 0;

        foreach (var record in thing.Records.Values)
        {
            if (record[column.Name] is not { } value) continue;

            inspected++;

            var key = TextComparison.AsCompared(column.Type, value)!;

            if (!byValue.TryGetValue(key, out var holders))
            {
                holders = [];
                byValue[key] = holders;
            }

            holders.Add(new RecordReference(thing.Definition.Name, record.Id));
        }

        return new UniquenessEffect(
            thing.Definition.Name,
            column.Name,
            inspected,
            [.. byValue.Values.Where(static holders => holders.Count > 1)]);
    }

    public void SetUnique(string collectionName, string columnName, bool unique)
    {
        var thing = Require(collectionName);
        Touch(thing.Definition.Name);
        var column = thing.Definition.Column(columnName)
            ?? throw new UnknownColumnException(
                thing.Definition.Name, StorageNames.Normalise(columnName, "column name"));

        if (column.Unique == unique) return;

        if (unique)
        {
            var effect = InspectUnique(thing.Definition.Name, column.Name);

            if (!effect.IsPossible)
            {
                throw new StorageValidationException(
                    [.. effect.Collisions.Select(holders => new DuplicateValue(
                        thing.Definition.Name, column.Name, null, holders[0].Id))]);
            }
        }

        Replace(thing, thing.Definition.WithColumns(
            [.. thing.Definition.Columns.Select(candidate => StorageNames.Same(candidate.Name, column.Name)
                ? new ColumnDefinition(
                    column.Name, column.Type, column.Purpose, column.Required, unique, column.ReadOnly,
                    column.DefaultValue)
                : candidate)]));
    }

    // ---- Relations (SC-8) --------------------------------------------------------------------

    public void AddRelation(RelationDefinition relation)
    {
        ArgumentNullException.ThrowIfNull(relation);

        var from = Require(relation.FromCollection).Definition;
        var to = Require(relation.ToCollection).Definition;

        var source = from.Column(relation.FromColumn)
            ?? throw new UnknownColumnException(from.Name, relation.FromColumn);
        var target = to.Column(relation.ToColumn)
            ?? throw new UnknownColumnException(to.Name, relation.ToColumn);

        if (!target.Unique)
        {
            throw new InvalidDefinitionException(
                "relation",
                $"'{relation.Name}' refers to '{to.Name}.{target.Name}', which two records can both hold - " +
                "so a reference to it would refer to nothing in particular.");
        }

        if (source.Type != target.Type)
        {
            throw new InvalidDefinitionException(
                "relation",
                $"'{relation.Name}' refers from a {source.Type} to a {target.Type}, and one cannot hold " +
                "the other.");
        }

        if (relation.Integrity is RelationIntegrity.SetEmpty && source.Required)
        {
            throw new InvalidDefinitionException(
                "relation",
                $"'{relation.Name}' says to empty '{source.Name}' when what it refers to goes, and " +
                $"'{source.Name}' has to have a value.");
        }

        if (!_relations.TryAdd(relation.Name, relation))
        {
            throw new RelationAlreadyExistsException(relation.Name);
        }
    }

    public bool RemoveRelation(string relationName) =>
        _relations.Remove(StorageNames.Normalise(relationName, "relation name"));

    public IReadOnlyCollection<RelationDefinition> GetRelations() => Shuffled(_relations.Values);

    // ---- The seams ---------------------------------------------------------------------------

    private DeletionEffect InspectDelete(Thing thing, Ulid id)
    {
        if (!thing.Records.TryGetValue(id, out var record)) return DeletionEffect.Nothing(false);

        var blocking = new List<RecordReference>();
        var removed = new List<RecordReference>();
        var cleared = new List<RecordReference>();

        foreach (var relation in Pointing(thing.Definition.Name))
        {
            if (record[relation.ToColumn] is not { } value) continue;

            var referring = Referring(relation, value);
            if (referring.Count == 0) continue;

            switch (relation.Integrity)
            {
                case RelationIntegrity.Cascade:
                    removed.AddRange(referring);
                    break;

                case RelationIntegrity.SetEmpty:
                    cleared.AddRange(referring);
                    break;

                default:
                    blocking.AddRange(referring);
                    break;
            }
        }

        return new DeletionEffect(true, blocking, removed, cleared);
    }

    private RelationDefinition? Refusing(Thing thing, Ulid id)
    {
        var record = thing.Records.GetValueOrDefault(id);

        foreach (var relation in Pointing(thing.Definition.Name))
        {
            if (relation.Integrity is RelationIntegrity.Cascade or RelationIntegrity.SetEmpty) continue;
            if (record?[relation.ToColumn] is not { } value) continue;
            if (Referring(relation, value).Count > 0) return relation;
        }

        return null;
    }

    private List<RecordReference> Referring(RelationDefinition relation, object value)
    {
        var source = Require(relation.FromCollection);
        var column = source.Definition.Column(relation.FromColumn)!;
        var wanted = TextComparison.AsCompared(column.Type, value);

        return
        [
            .. source.Records.Values
                .Where(record => Equals(TextComparison.AsCompared(column.Type, record[column.Name]), wanted))
                .Select(record => new RecordReference(source.Definition.Name, record.Id))
        ];
    }

    private bool Clear(RecordReference reference, string targetCollection)
    {
        var relation = _relations.Values.FirstOrDefault(candidate =>
            StorageNames.Same(candidate.FromCollection, reference.CollectionName)
            && StorageNames.Same(candidate.ToCollection, targetCollection)
            && candidate.Integrity is RelationIntegrity.SetEmpty);

        if (relation is null) return false;

        var record = Require(reference.CollectionName).Records.GetValueOrDefault(reference.Id);

        return record is not null && Update(record.With(relation.FromColumn, null));
    }

    private IEnumerable<RelationDefinition> From(string collectionName) =>
        _relations.Values.Where(relation => StorageNames.Same(relation.FromCollection, collectionName));

    /// <summary>Whether anything in the collection referred to holds this value. A scan, honestly.</summary>
    private bool Exists(RelationDefinition relation, object value)
    {
        var target = Require(relation.ToCollection);
        var column = target.Definition.Column(relation.ToColumn);
        if (column is null) return false;

        var wanted = TextComparison.AsCompared(column.Type, value);

        return target.Records.Values.Any(record =>
            Equals(TextComparison.AsCompared(column.Type, record[column.Name]), wanted));
    }

    private IEnumerable<RelationDefinition> Pointing(string collectionName) =>
        _relations.Values.Where(relation => StorageNames.Same(relation.ToCollection, collectionName));

    private RelationDefinition? Relation(string relationName) =>
        _relations.GetValueOrDefault(StorageNames.Normalise(relationName, "relation name"));

    private static int Excluded(Thing thing, ValidatedQuery validated)
    {
        var asked = validated.Where.Select(static condition => condition.Column.Name)
            .Concat(validated.OrderBy.Select(static entry => entry.Column.Name))
            .Distinct(StorageNames.Comparer)
            .ToHashSet(StorageNames.Comparer);

        return thing.Records.Values.Count(record => record.NeedsAttention.Any(asked.Contains));
    }

    private static QueryExecutionInfo Info(
        QueryAccess access,
        Thing thing,
        string? columnName,
        string description,
        int examined,
        int returned,
        int excluded,
        TimeSpan elapsed) =>
        new(access, thing.Definition.Name, columnName, description, examined, returned, excluded, 0, elapsed);

    private void Replace(Thing thing, CollectionDefinition definition) =>
        _things[definition.Name] = thing with { Definition = definition };

    private void Rewrite(
        Thing thing,
        CollectionDefinition definition,
        Action<Dictionary<string, object?>, HashSet<string>> change,
        string? removedColumn = null,
        (string From, string To)? renamed = null,
        string? retypedColumn = null)
    {
        StorageRecord Changed(Ulid id, StorageRecord record)
        {
            var fields = new Dictionary<string, object?>(record.Fields, StorageNames.Comparer);
            var attention = new HashSet<string>(record.NeedsAttention, StorageNames.Comparer);

            change(fields, attention);

            return new StorageRecord(id, definition.Name, fields, attention);
        }

        var records = thing.Records.ToDictionary(static entry => entry.Key, entry => Changed(entry.Key, entry.Value));

        // Every kept version is read through the current shape, as the engine reads a stored
        // version through the changes since it was written - and keeps beside it what the shape
        // drops (TR-6a): a removed field's value, and a retyped value as it was written.
        MemoryVersion Carried(Ulid id, MemoryVersion version)
        {
            if (version.Snapshot is null) return version;

            var removed = new Dictionary<string, object?>(version.Removed, StorageNames.Comparer);
            var asWritten = new Dictionary<string, object?>(version.AsWritten, StorageNames.Comparer);

            if (removedColumn is not null && version.Snapshot.Fields.TryGetValue(removedColumn, out var lost)) removed[removedColumn] = lost;
            if (renamed is { } move && asWritten.Remove(move.From, out var written)) asWritten[move.To] = written;
            if (retypedColumn is not null && version.Snapshot.Fields.TryGetValue(retypedColumn, out var value) && !asWritten.ContainsKey(retypedColumn)) asWritten[retypedColumn] = value;

            return version with { Snapshot = Changed(id, version.Snapshot), Removed = removed, AsWritten = asWritten };
        }

        var histories = thing.Histories.ToDictionary(
            static entry => entry.Key,
            entry => entry.Value.Select(version => Carried(entry.Key, version)).ToList());

        var names = thing.Renamed.ToDictionary(static entry => entry.Key, static entry => entry.Value.ToList(), StorageNames.Comparer);
        if (renamed is { } rename)
        {
            var earlier = names.Remove(rename.From, out var was) ? was : [];
            earlier.Add(rename.From);
            names[rename.To] = earlier;
        }

        if (removedColumn is not null) names.Remove(removedColumn);

        _things[definition.Name] = new Thing(definition, records, histories) { LastChanged = thing.LastChanged, Renamed = names };
    }

    // ---- Versions (SC-12): a full copy per version, which changes the cost and not the meaning --

    public Ulid? HeadVersion(string collectionName, Ulid id) =>
        Require(collectionName).Histories.TryGetValue(id, out var history) && history.Count > 0
            ? history[^1].Version
            : null;

    public bool Keeps(string collectionName, Ulid id, Ulid versionId) =>
        Require(collectionName).Histories.TryGetValue(id, out var history)
        && history.Any(version => version.Version == versionId);

    /// <summary>
    /// SC-12, TR-6a: through the current shape, carrying what it cannot show - a removed field's
    /// values, a retyped value as it was written, and the name a renamed field had then.
    /// </summary>
    public VersionDifference DiffVersions(string collectionName, Ulid id, Ulid fromVersionId, Ulid toVersionId)
    {
        var thing = Require(collectionName);
        var from = Version(thing, id, fromVersionId);
        var to = Version(thing, id, toVersionId);

        Dictionary<string, object?> Shown(MemoryVersion version)
        {
            var fields = new Dictionary<string, object?>(version.Snapshot?.Fields ?? new Dictionary<string, object?>(StorageNames.Comparer), StorageNames.Comparer);
            if (version.Snapshot is null) return fields;
            foreach (var (column, value) in version.AsWritten) fields[column] = value;
            foreach (var (column, value) in version.Removed) fields[column] = value;
            return fields;
        }

        var before = Shown(from);
        var after = Shown(to);

        var changes = new List<ColumnChange>();
        foreach (var column in before.Keys.Concat(after.Keys).Distinct(StorageNames.Comparer).OrderBy(static column => column, StringComparer.Ordinal))
        {
            var was = before.GetValueOrDefault(column);
            var now = after.GetValueOrDefault(column);
            if (Equals(was, now)) continue;

            var current = thing.Definition.Column(column);
            var removed = current is null || from.Removed.ContainsKey(column) || to.Removed.ContainsKey(column);
            var renamedFrom = thing.Renamed.TryGetValue(column, out var names) && names.Count > 0 ? names[0] : null;
            var kept = (was ?? now) is { } value && ColumnTypes.TryRecordedType(value, out var recorded) ? recorded : (ColumnType?)null;
            var retyped = !removed && current is not null && kept is not null && kept != current.Type;

            changes.Add(new ColumnChange(column, was, now)
            {
                Fate = removed ? FieldFate.SinceRemoved
                    : (renamedFrom is not null ? FieldFate.SinceRenamed : FieldFate.Kept) | (retyped ? FieldFate.SinceRetyped : FieldFate.Kept),
                WasCalled = removed ? null : renamedFrom,
                KeptThen = retyped ? kept : null
            });
        }

        return new VersionDifference(thing.Definition.Name, id, fromVersionId, toVersionId, changes,
            from.Snapshot is null, to.Snapshot is null);
    }

    public StorageRecord RestoreVersion(string collectionName, Ulid id, Ulid versionId)
    {
        var thing = Require(collectionName);
        var version = Version(thing, id, versionId);

        if (version.Snapshot is null) throw new VersionNotRestorableException(thing.Definition.Name, id, versionId);

        var existing = thing.Records.GetValueOrDefault(id);
        if (existing is not null && HeadVersion(collectionName, id) == versionId) return existing;

        // Judged as a write is judged, except that the record's own value in a unique column is
        // not a duplicate of itself.
        var judged = SharedRules.ForCreate(thing.Definition, version.Snapshot.Fields,
            (column, value) => thing.HolderOf(column, value) is { } holder && holder != id ? holder : null);

        SharedRules.CheckReferences(thing.Definition, judged, From(thing.Definition.Name), Exists);

        var restored = new StorageRecord(id, thing.Definition.Name, judged, version.Snapshot.NeedsAttention);
        thing.Records[id] = restored;
        thing.Remember(id, restored);
        thing.LastChanged = DateTimeOffset.UtcNow;
        return restored;
    }

    /// <summary>
    /// V-15 of the versioning plan on a list: the version the record was at before the moment
    /// stays, with everything after it; the rest goes.
    /// </summary>
    public int PurgeRecordHistory(string collectionName, Ulid id, DateTimeOffset before)
    {
        var thing = Require(collectionName);
        if (!thing.Histories.TryGetValue(id, out var history)) return 0;

        var atMoment = history.LastOrDefault(version => version.Version.Time <= before);
        var kept = history.Where(version => version.Version.Time > before || version == atMoment).ToList();
        var removed = history.Count - kept.Count;

        thing.Histories[id] = kept;
        return removed;
    }

    public bool Erase(string collectionName, Ulid id)
    {
        var thing = Require(collectionName);
        var held = thing.Records.Remove(id);
        var erased = thing.Histories.Remove(id) || held;
        if (erased) thing.LastChanged = DateTimeOffset.UtcNow;
        return erased;
    }

    // ---- The overview (BR-1, BR-1a) -------------------------------------------------------------

    public IReadOnlyList<StoredThing> Overview() =>
    [
        .. _things.Values
            .Select(static thing => new StoredThing(thing.Definition, thing.Records.Count, thing.LastChanged))
            .OrderByDescending(static thing => thing.LastChanged ?? DateTimeOffset.MinValue)
            .ThenBy(static thing => thing.Name, StringComparer.Ordinal)
    ];

    public StoredThing? Describe(string collectionName) =>
        _things.TryGetValue(Name(collectionName), out var thing)
            ? new StoredThing(thing.Definition, thing.Records.Count, thing.LastChanged)
            : null;

    /// <summary>A dictionary's count is never out of step with its records, so there is never anything to reconcile.</summary>
    public int ReconcileOverview() => 0;

    private static MemoryVersion Version(Thing thing, Ulid id, Ulid versionId)
    {
        if (thing.Histories.TryGetValue(id, out var history)
            && history.FirstOrDefault(version => version.Version == versionId) is { } found)
        {
            return found;
        }

        throw new VersionNotKeptException(thing.Definition.Name, id, versionId);
    }

    /// <summary>When it last changed (BR-1a): recorded when the change is made, on the thing under that name at the end of the unit of work.</summary>
    private void Touch(string collectionName) => _touched.Add(collectionName);

    private readonly HashSet<string> _touched = new(StorageNames.Comparer);

    private void FlushTouched()
    {
        foreach (var name in _touched)
        {
            if (_things.TryGetValue(name, out var thing)) thing.LastChanged = DateTimeOffset.UtcNow;
        }

        _touched.Clear();
    }

    private Thing Require(string collectionName)
    {
        var name = Name(collectionName);
        return _things.TryGetValue(name, out var thing) ? thing : throw new UnknownCollectionException(name);
    }

    /// <summary>
    /// Trims a name the way a definition would, so that looking a collection up by the name it
    /// was created with works whatever whitespace came with it - and so that a name that is not
    /// a name is refused here too rather than reported as "no such collection".
    /// </summary>
    private static string Name(string collectionName) =>
        new CollectionDefinition(collectionName).Name;

    private IReadOnlyCollection<T> Shuffled<T>(IEnumerable<T> source)
    {
        var items = source.ToArray();

        for (var i = items.Length - 1; i > 0; i--)
        {
            var j = _order.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }

        return items;
    }

    private static Dictionary<string, Thing> Copy(Dictionary<string, Thing> things) =>
        things.ToDictionary(
            static entry => entry.Key,
            static entry => new Thing(
                entry.Value.Definition,
                new Dictionary<Ulid, StorageRecord>(entry.Value.Records),
                entry.Value.Histories.ToDictionary(static history => history.Key, static history => new List<MemoryVersion>(history.Value)))
            {
                LastChanged = entry.Value.LastChanged
            },
            StorageNames.Comparer);

    /// <summary>One version of one record: the record as it then was, or null for its deletion.</summary>
    /// <summary>
    /// One kept version: the snapshot as the current shape reads it, plus what that shape cannot
    /// carry (TR-6a) - the values of fields since removed, and the values as they were before a
    /// retype converted them - so that a diff shows a version as it was.
    /// </summary>
    private sealed record MemoryVersion(Ulid Version, StorageRecord? Snapshot)
    {
        public Dictionary<string, object?> Removed { get; init; } = new(StorageNames.Comparer);
        public Dictionary<string, object?> AsWritten { get; init; } = new(StorageNames.Comparer);
    }

    private sealed record Thing(
        CollectionDefinition Definition,
        Dictionary<Ulid, StorageRecord> Records,
        Dictionary<Ulid, List<MemoryVersion>> Histories)
    {
        /// <summary>When it last changed (BR-1a): moved by every write, as the engine moves its own.</summary>
        public DateTimeOffset? LastChanged { get; set; }

        /// <summary>What each current field was called before, oldest name first, as the engine's descriptor keeps its renames.</summary>
        public Dictionary<string, List<string>> Renamed { get; init; } = new(StorageNames.Comparer);

        /// <summary>
        /// A new version of the record, with an identity from the same source records get, so
        /// that versions and records sort together in the order they were made.
        /// </summary>
        public void Remember(Ulid id, StorageRecord? snapshot)
        {
            if (!Histories.TryGetValue(id, out var history)) Histories[id] = history = [];
            history.Add(new MemoryVersion(RecordIdentity.Next(), snapshot));
        }

        /// <summary>Removes the record and records its deletion as a version.</summary>
        public bool Forget(Ulid id)
        {
            if (!Records.Remove(id)) return false;
            Remember(id, null);
            LastChanged = DateTimeOffset.UtcNow;
            return true;
        }

        /// <summary>
        /// The record already holding a value in a unique column. A scan, which is the honest
        /// thing for a dictionary; the engine will seek an index for the same answer.
        /// </summary>
        public Ulid? HolderOf(ColumnDefinition column, object value)
        {
            // Compared the way the engine's unique index compares, which for text means folded:
            // see TextComparison. Two receipt numbers differing only in case are one receipt
            // number to the index that enforces uniqueness, so they have to be one here too.
            var wanted = TextComparison.AsCompared(column.Type, value);

            foreach (var record in Records.Values)
            {
                if (Equals(TextComparison.AsCompared(column.Type, record[column.Name]), wanted))
                {
                    return record.Id;
                }
            }

            return null;
        }
    }
}
