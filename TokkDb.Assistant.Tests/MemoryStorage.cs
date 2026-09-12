using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// <see cref="IStorage"/> in dictionaries.
///
/// It is not a stub. It obeys the contract, including the parts that are easy to skip - a
/// refused write leaves nothing behind, a failed unit of work leaves nothing behind, and a value
/// comes back as the type it went in as - because a fake that is easier to satisfy than the real
/// thing makes the contract suite worth less than the time it takes to run.
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
/// </summary>
public sealed class MemoryStorage : IStorage
{
    private readonly Random _order = new(20260912);

    private Dictionary<string, Thing> _things = new(StorageNames.Comparer);

    private Dictionary<string, Thing>? _before;
    private int _depth;

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
        }

        try
        {
            var result = work();

            if (--_depth == 0)
            {
                _before = null;
            }

            return result;
        }
        catch
        {
            if (--_depth == 0)
            {
                _things = _before!;
                _before = null;
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

        _things[definition.Name] = new Thing(definition, []);
    }

    public CollectionDefinition? GetCollectionDefinition(string collectionName) =>
        _things.TryGetValue(Name(collectionName), out var thing) ? thing.Definition : null;

    public IReadOnlyCollection<CollectionDefinition> GetCollectionDefinitions() =>
        Shuffled(_things.Values.Select(static thing => thing.Definition));

    public bool DeleteCollection(string collectionName) => _things.Remove(Name(collectionName));

    public StorageRecord Create(string collectionName, IReadOnlyDictionary<string, object?> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var thing = Require(collectionName);
        var judged = SharedRules.ForCreate(thing.Definition, fields, thing.HolderOf);
        var record = new StorageRecord(RecordIdentity.Next(), thing.Definition.Name, judged);

        thing.Records[record.Id] = record;
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
        thing.Records[record.Id] = new StorageRecord(record.Id, thing.Definition.Name, judged);
        return true;
    }

    public bool Delete(string collectionName, Ulid id) => Require(collectionName).Records.Remove(id);

    public IReadOnlyCollection<StorageRecord> GetAll(string collectionName) =>
        Shuffled(Require(collectionName).Records.Values);

    /// <summary>
    /// SC-7 without a planner. There is no index here and there is no honest way to report one:
    /// a dictionary of records answers every condition by looking at every record, so a query
    /// says so - except a lookup by identity, which really is one, because the records are held
    /// by identity.
    ///
    /// Reporting a scan as a scan is the point. A fake that claimed a seek would let a caller
    /// believe a question is cheap here and discover against the engine that it is not.
    /// </summary>
    public StorageQueryResult ExecuteQuery(StorageQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var thing = Require(query.CollectionName);
        var validated = QueryValidation.Against(thing.Definition, query);

        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        var byIdentity = query.Ids.Count > 0;
        var candidates = byIdentity
            ? query.Ids.Select(thing.Records.GetValueOrDefault).OfType<StorageRecord>().ToArray()
            : Shuffled(thing.Records.Values).ToArray();

        var matched = candidates
            .Where(record => validated.Where.All(condition => QueryMatching.Matches(record, condition)))
            .ToArray();

        var page = QueryMatching.Ordered(matched, validated.OrderBy).Skip(query.Skip);
        if (query.Take is { } take) page = page.Take(take);

        var records = page.ToList();

        var path = byIdentity
            ? new QueryAccessPath(
                QueryAccessPathKind.IdentityLookup,
                thing.Definition.Name,
                null,
                $"lookup of {query.Ids.Count} records by identity in {thing.Definition.Name}")
            : new QueryAccessPath(
                QueryAccessPathKind.FullScan,
                thing.Definition.Name,
                null,
                $"full scan of {thing.Definition.Name} (nothing here is indexed)");

        return new StorageQueryResult(
            thing.Definition.Name,
            records,
            path,
            new QueryCost(
                candidates.Length,
                records.Count,
                PagesRead: 0,
                System.Diagnostics.Stopwatch.GetElapsedTime(started)));
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
        Replace(thing, StructuralChange.Add(thing.Definition, column));
    }

    public void RenameColumn(string collectionName, string columnName, string newName)
    {
        var thing = Require(collectionName);
        var before = thing.Definition;
        var after = StructuralChange.Rename(before, columnName, newName);
        if (ReferenceEquals(before, after)) return;

        var from = StorageNames.Normalise(columnName, "column name");
        var to = StorageNames.Normalise(newName, "column name");

        Rewrite(thing, after, fields =>
        {
            // Only a column the record actually had moves. One it never had stays not there,
            // rather than appearing under the new name holding nothing.
            if (fields.Remove(from, out var value)) fields[to] = value;
        });
    }

    public void RetypeColumn(string collectionName, string columnName, ColumnType newType)
    {
        var thing = Require(collectionName);
        var before = thing.Definition;
        var after = StructuralChange.Retype(before, columnName, newType);
        if (ReferenceEquals(before, after)) return;

        var name = StorageNames.Normalise(columnName, "column name");
        var was = before.Column(name)!.Type;

        Rewrite(thing, after, fields =>
        {
            if (fields.TryGetValue(name, out var value))
            {
                fields[name] = ColumnConversion.Retype(was, newType, value);
            }
        });
    }

    public void RemoveColumn(string collectionName, string columnName)
    {
        var thing = Require(collectionName);
        var after = StructuralChange.Remove(thing.Definition, columnName);
        var name = StorageNames.Normalise(columnName, "column name");

        Rewrite(thing, after, fields => fields.Remove(name));
    }

    /// <summary>
    /// Nothing to converge: every change was applied when it was made. Zero is the honest
    /// answer, and it is the same answer the engine gives once it has caught up.
    /// </summary>
    public int Converge(string collectionName)
    {
        Require(collectionName);
        return 0;
    }

    private void Replace(Thing thing, CollectionDefinition definition) =>
        _things[definition.Name] = thing with { Definition = definition };

    private void Rewrite(Thing thing, CollectionDefinition definition, Action<Dictionary<string, object?>> change)
    {
        var records = new Dictionary<Ulid, StorageRecord>();

        foreach (var (id, record) in thing.Records)
        {
            var fields = new Dictionary<string, object?>(record.Fields, StorageNames.Comparer);
            change(fields);
            records[id] = new StorageRecord(id, definition.Name, fields);
        }

        _things[definition.Name] = new Thing(definition, records);
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
            static entry => new Thing(entry.Value.Definition, new Dictionary<Ulid, StorageRecord>(entry.Value.Records)),
            StorageNames.Comparer);

    private sealed record Thing(CollectionDefinition Definition, Dictionary<Ulid, StorageRecord> Records)
    {
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
