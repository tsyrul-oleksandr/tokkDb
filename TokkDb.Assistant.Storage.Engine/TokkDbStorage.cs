using System.Diagnostics;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Trace;
using TokkDb.Documents;
using TokkDb.Documents.Keys;
using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Path.Normalization;
using TokkDb.Pages;
using TokkDb.Pages.Indexes;
using TokkDb.Pages.Records;
using TokkDb.Pages.Relations;
using TokkDb.Pages.Versions;
using EngineConnection = TokkDb.TokkDbConnection;
using EngineEntities = TokkDb.DbEntities<System.Collections.Generic.Dictionary<string, object?>>;

namespace TokkDb.Assistant.Storage.Engine;

/// <summary>
/// <see cref="IStorage"/> over <c>TokkDbConnection</c>.
///
/// D-2, as literally as it can be taken. The engine owns storage - pages, the catalogue, the
/// indexes, the transactions - and knows nothing of a <see cref="CollectionDefinition"/>. This
/// owns the logical schema and the judgement, and it reaches for an engine facility rather than
/// building a second one: definitions live in the catalogue, metadata and display rules in the
/// engine's settings documents, uniqueness in a unique index, relations in the relation
/// catalogue, conversations in the reserved collections, and a unit of work is
/// <c>InTransaction</c>.
///
/// <b>What is not here.</b> No rule about what a value may be, and no identity minted. Both come
/// from <c>SharedRules</c> and the engine, so that this and <c>MemoryStorage</c> cannot answer
/// the same question differently - which is the disagreement §2.2 of the engine plan recorded
/// and the reason one contract suite runs against both.
///
/// <b>The one thing taken out of the engine's hands, and why.</b> Renaming and removing a column
/// are recorded as engine migration steps and replayed lazily on read, exactly as DC-7 intends.
/// Retyping is not, because the engine's <c>ValueMigration</c> turns a value it cannot read into
/// null and SC-6b forbids that: the value is the only copy and a person has to see it to decide
/// what it should be. So the retype is kept in the definition and applied on read here, in the
/// same lazy shape - converted where it converts, kept and flagged where it does not, converged
/// on demand. Everything else about lazy migration is still the engine's.
///
/// The old <c>TokkDb.LLM.Storage.Engine</c> is worth reading and is referenced by nothing here.
/// Three things it found are taken and one is corrected: a field map needs its own serializer
/// carrying the column types; a lookup by identity is the primary-index path and should go
/// through the planner rather than round it; the engine reports a missing record by throwing
/// where the contract reports it by returning false. What is corrected is that it validated
/// nothing on the way in - SC-3 is the requirement that says it must.
/// </summary>
public sealed class TokkDbStorage : IStorage, IDisposable
{
    /// <summary>
    /// Below this many values, checking them one at a time is the cheaper path and no index is
    /// built for the occasion (SC-9a). Above it, the set is worth one ordered walk.
    /// </summary>
    private const int OrderedPassThreshold = 16;

    /// <summary>
    /// How many records there have to be per value asked about before a seek per value beats one
    /// walk (SC-9a).
    ///
    /// <b>Measured rather than chosen, and the number is smaller than SC-9 expects.</b> A seek
    /// costs three page reads on a tree of this height and a fourth for the record. A walk over
    /// the range costs, through the engine's public surface, <b>one read per record in it</b> -
    /// measured at 20,114 page reads for 20,000 records - because the planner reads every record
    /// the range covers and the engine has no page cache to make the second read of a page free.
    /// So the crossing point is about four records per value, not the far higher figure it would
    /// be if the walk touched only index pages.
    ///
    /// <b>What that says about SC-9, raised rather than worked around (D-2).</b> The requirement's
    /// reasoning is right and its bound needs something the engine does not expose: a way to read
    /// index entries without reading the records they point at. <c>SecondaryIndex.Find</c> is
    /// exactly that and is not reachable from <c>TokkDbConnection</c>. With it, the ordered pass
    /// would cost the leaf pages of the range - about a thirtieth of what it costs now - and
    /// would beat seeks everywhere. That is a second engine change, and this plan allows one, so
    /// the shape of the contract is built as SC-9 asks and the path is chosen by which is
    /// actually cheaper today.
    /// </summary>
    private const int SeekRatio = 4;

    private readonly EngineConnection _connection;
    private readonly EngineConversations _conversations;
    private readonly EngineTraces _traces;

    /// <summary>Opens or creates the database at <paramref name="databaseFilePath"/>.</summary>
    public TokkDbStorage(string databaseFilePath)
    {
        _connection = new EngineConnection(databaseFilePath);
        _connection.Load();
        _conversations = new EngineConversations(_connection);
        _traces = new EngineTraces(_connection);
        EnsureVersioned();
    }

    /// <summary>Takes a connection someone else opened, and does not close it.</summary>
    public TokkDbStorage(EngineConnection connection, bool ownsConnection = false)
    {
        _connection = connection;
        Borrowed = !ownsConnection;
        _conversations = new EngineConversations(_connection);
        _traces = new EngineTraces(_connection);
        EnsureVersioned();
    }

    /// <summary>
    /// SC-12a: every user collection keeps versions, whatever the engine's default was when it
    /// was made. A collection still at <c>None</c> is switched on here, at open, in one unit of
    /// work; switching on rewrites no record - a record gains history only when it is next
    /// changed, with a <c>Baseline</c> of what it was. A storage opened on a read-only connection
    /// cannot switch anything and leaves it, so that reading is still possible.
    /// </summary>
    private void EnsureVersioned()
    {
        if (_connection.AccessMode != Disk.TokkDbAccessMode.ReadWrite) return;

        var unversioned = _connection.Collections
            .Where(static descriptor => !descriptor.IsSystem && descriptor.RetentionPolicy != RetentionPolicy.KeepVersions)
            .Select(static descriptor => descriptor.Name)
            .ToList();

        if (unversioned.Count == 0) return;

        _connection.InTransaction(() =>
        {
            foreach (var name in unversioned)
            {
                _connection.SetRetentionPolicy(name, RetentionPolicy.KeepVersions);
            }
        });
    }

    private bool Borrowed { get; }

    public IConversationStore Conversations => _conversations;

    /// <summary>
    /// Where this database's traces and its change journal are (D-8, TR-4).
    ///
    /// Not on <see cref="IStorage"/>, and that is §3.1 rather than an oversight: the storage
    /// contract depends on nothing, the trace model is its own project, and a contract that
    /// mentioned a <see cref="DataChange"/> would drag one into the other. What makes TR-4
    /// possible is that this recorder is on the same connection - so a change recorded inside
    /// <see cref="InUnitOfWork(Action)"/> commits in the transaction of the mutation it
    /// describes, with nothing arranging it.
    /// </summary>
    public ITraceRecorder Traces => _traces;

    // ---- The unit of work -----------------------------------------------------------------

    /// <summary>
    /// SC-5, which the engine already had: TX-1's atomicity offered to the caller rather than
    /// applied one operation at a time. An import of five hundred records pays the commit
    /// protocol once instead of five hundred times, and a failure half way through is rolled
    /// back through the journal rather than left behind.
    ///
    /// A unit of work inside one joins it, because the engine's own transactions nest that way.
    /// </summary>
    public void InUnitOfWork(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        _connection.InTransaction(work);
    }

    public T InUnitOfWork<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        var result = default(T)!;
        _connection.InTransaction(() => result = work());
        return result;
    }

    // ---- Collections ----------------------------------------------------------------------

    public void CreateCollection(CollectionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        SharedRules.CheckDefinitionFitsItself(definition);

        if (Find(definition.Name) is not null)
        {
            throw new CollectionAlreadyExistsException(definition.Name);
        }

        // One transaction for all three, so that a collection cannot exist with half of its
        // definition: the descriptor, the settings the descriptor has no room for, and the
        // display rule. The engine's own CreateCollection opens a nested transaction, which
        // joins this one.
        _connection.InTransaction(() =>
        {
            _connection.CreateCollection(
                definition.Name,
                EngineSchema.ToEngineColumns(definition),
                definition.Purpose ?? string.Empty);

            // SC-12a: versioned whatever the engine's default is, said here rather than assumed.
            if (_connection.Collection(definition.Name).RetentionPolicy != RetentionPolicy.KeepVersions)
            {
                _connection.SetRetentionPolicy(definition.Name, RetentionPolicy.KeepVersions);
            }

            var settings = EngineSchema.ToSettings(definition);
            if (settings.Count > 0)
            {
                _connection.SetMetadata(definition.Name, settings);
            }

            if (definition.DisplayRule is not null)
            {
                _connection.SetDisplayRule(definition.Name, definition.DisplayRule.Template);
            }
        });
    }

    public CollectionDefinition? GetCollectionDefinition(string collectionName)
    {
        var descriptor = Find(Name(collectionName));
        return descriptor is null ? null : ToDefinition(descriptor);
    }

    public IReadOnlyCollection<CollectionDefinition> GetCollectionDefinitions() =>
        _connection.Collections
            .Where(static descriptor => !descriptor.IsSystem)
            .Select(ToDefinition)
            .ToList();

    public bool DeleteCollection(string collectionName) => _connection.DropCollection(Name(collectionName));

    public void SetMetadata(string collectionName, IReadOnlyDictionary<string, string?> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var definition = Require(collectionName);

        // Written whole, with the pending counts carried over: they are the storage's own
        // bookkeeping and have nothing to do with what the caller wants remembered.
        _connection.SetMetadata(
            definition.Name,
            EngineSchema.ToSettings(definition.WithMetadata(metadata), Pending(definition.Name)));
    }

    public void SetDisplayRule(string collectionName, DisplayRule? displayRule)
    {
        var definition = Require(collectionName);

        SharedRules.CheckDefinitionFitsItself(definition.WithDisplayRule(displayRule));

        _connection.SetDisplayRule(definition.Name, displayRule?.Template ?? string.Empty);
    }

    // ---- Records --------------------------------------------------------------------------

    public StorageRecord Create(string collectionName, IReadOnlyDictionary<string, object?> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var definition = Require(collectionName);
        var judged = SharedRules.ForCreate(definition, fields, HolderOf(definition));

        SharedRules.CheckReferences(definition, judged, From(definition.Name), Exists);

        var id = Entities(definition).Insert(judged);

        return new StorageRecord(id, definition.Name, judged);
    }

    public StorageRecord? GetById(string collectionName, Ulid id)
    {
        var definition = Require(collectionName);
        var found = Entities(definition).GetById(id);

        return found is null ? null : EngineRecords.ToRecord(definition.Name, id, found.Value);
    }

    public bool Update(StorageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var definition = Require(record.CollectionName);
        var entities = Entities(definition);

        var found = entities.GetById(record.Id);
        if (found is null)
        {
            return false;
        }

        var existing = EngineRecords.ToRecord(definition.Name, record.Id, found.Value);
        var judged = SharedRules.ForUpdate(definition, record, existing, HolderOf(definition));

        SharedRules.CheckReferences(definition, judged, From(definition.Name), Exists);

        // What this write does to the count of values still waiting to be made sense of. A
        // record that had one and does not any more is one fewer, and the count is what a query
        // on that column reports as not considered (SC-6c).
        var before = EngineRecords.NotOfColumnType(found.Value).Select(static entry => entry.Column);
        var after = judged.Where(entry => IsOfAnotherType(definition, entry.Key, entry.Value))
            .Select(static entry => entry.Key);

        _connection.InTransaction(() =>
        {
            entities.Update(record.Id, judged);
            AdjustPending(definition.Name, Difference(before, after));
        });

        return true;
    }

    public DeletionResult Delete(string collectionName, Ulid id)
    {
        var definition = Require(collectionName);
        var effect = InspectDelete(definition, id);

        if (!effect.RecordExists) return DeletionResult.NotFound;

        if (effect.Blocking.Count > 0)
        {
            var relation = Refusing(definition.Name, id)!;
            throw new IntegrityRefusedException(relation, new RecordReference(definition.Name, id), effect.Blocking);
        }

        var removed = new List<RecordReference>();
        var cleared = new List<RecordReference>();

        // One unit of work for the record and everything that goes with it, so that a cascade
        // cannot half happen - and so that the caller can record each deletion separately
        // knowing all of them committed together (SC-8a).
        _connection.InTransaction(() =>
        {
            foreach (var reference in effect.WouldAlsoBeRemoved)
            {
                if (Remove(Require(reference.CollectionName), reference.Id)) removed.Add(reference);
            }

            foreach (var reference in effect.WouldBeCleared)
            {
                if (Clear(reference, definition.Name)) cleared.Add(reference);
            }

            Remove(definition, id);
        });

        return new DeletionResult(true, removed, cleared);
    }

    public DeletionEffect InspectDelete(string collectionName, Ulid id) =>
        InspectDelete(Require(collectionName), id);

    public IReadOnlyCollection<StorageRecord> GetAll(string collectionName)
    {
        var definition = Require(collectionName);

        return Entities(definition).GetAllRecords()
            .Select(record => EngineRecords.ToRecord(definition.Name, record.RecordId, record.Value))
            .ToList();
    }

    // ---- Queries (SC-7) ---------------------------------------------------------------------

    /// <summary>
    /// SC-7, through the engine's planner. The query is checked against the schema first, so a
    /// query that does not fit costs nothing, and then it goes to the planner as conjuncts.
    ///
    /// Planned twice on purpose. <c>Explain</c> gives the access path as a structure - which
    /// index, which column - and running gives the records and what they cost, and the engine's
    /// public surface offers no call that gives both. Planning reads the index catalogue and no
    /// data, so the second one costs a lookup rather than a read; parsing the kind back out of
    /// the report's description would cost nothing and break the first time a description was
    /// reworded.
    /// </summary>
    public StorageQueryResult ExecuteQuery(StorageQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var definition = Require(query.CollectionName);

        if (!QueryTraversals.TryResolve(query, Relation, ExecuteQuery, out var resolved))
        {
            return Nothing(definition, query, "nothing on the other side of the relation matched");
        }

        var started = Stopwatch.GetTimestamp();

        // The cursor as a bound the planner can use: everything at or after the value the last
        // page ended on, which is an index range rather than a scan. The exact boundary - which
        // of the records holding that value have already been seen - is settled below, because
        // the pair of value and identity is what a cursor is and the engine has no operator for
        // a pair. See QueryCursor.
        //
        // Not pushed down when the query asks for totals: an aggregate is over everything that
        // matched, and a bound would make it over the rest of the pages instead.
        var bounded = Bound(resolved, definition);

        var validated = QueryValidation.Against(definition, bounded);
        var normalized = EngineQuery.ToNormalized(validated);

        // Same story as the residual: the engine takes a null id list to mean "no restriction on
        // identity" while declaring the parameter non-nullable.
        var ids = query.Ids.Count == 0 ? null! : query.Ids;
        var entities = Entities(definition);

        var plan = entities.Explain(normalized, ids);
        var result = entities.Query(normalized, ids);

        var matched = result.Records
            .Select(record => EngineRecords.ToRecord(definition.Name, record.RecordId, record.Value))
            .ToList();

        if (resolved.After is { } cursor && validated.OrderBy.Count > 0)
        {
            matched = [.. matched.Where(record => QueryMatching.After(record, validated.OrderBy, cursor))];
        }

        var aggregates = QueryMatching.Aggregate(matched, validated.Aggregates);

        var page = QueryMatching.Ordered(matched, validated.OrderBy).Skip(query.Skip);
        if (query.Take is { } take) page = page.Take(take + 1);

        var records = page.ToList();

        // One more than the page was asked for, so that "is there another page" is answered
        // without a second query and without a count.
        var more = query.Take is { } wanted && records.Count > wanted;
        if (more) records.RemoveAt(records.Count - 1);

        var next = more && validated.OrderBy.Count > 0
            ? QueryMatching.CursorFor(records[^1], validated.OrderBy)
            : null;

        var projected = validated.Select.Count == 0
            ? records
            : [.. records.Select(record => QueryMatching.Project(record, validated.Select))];

        return new StorageQueryResult(
            definition.Name,
            projected,
            EngineQuery.ToExecutionInfo(
                plan.Path,
                result.Report.RecordsExamined,
                projected.Count,
                Excluded(definition, validated),
                result.Report.PagesRead,
                Stopwatch.GetElapsedTime(started)),
            aggregates,
            next);
    }

    /// <summary>
    /// SC-9: one set of values in, one ordered walk of the index, the records that hold them out.
    ///
    /// <b>Why the shape matters more than the code.</b> The engine has no page cache -
    /// <c>DiskReader.ReadPage</c> allocates a buffer and reads the file every time - so ten
    /// thousand lookups are ten thousand physical reads multiplied by the height of the tree. The
    /// values are therefore encoded with the engine's own key encoder, sorted into key order, and
    /// asked for as one range: a descent to the first of them and then the leaf chain, which is
    /// sequential.
    ///
    /// <b>What the B+Tree actually does, which step 1.5 asks to be recorded.</b> Its leaves are
    /// chained and <c>Range</c> descends once and then follows <c>NextPageIndex</c> - its own
    /// comment says so: "one descent to the left-hand end and then a walk along the chain,
    /// touching each leaf once and no interior node at all". So the bound is the pages of the
    /// range, not one page per value, and it does not grow when more values are asked about.
    ///
    /// <b>And what it is not, raised rather than assumed (D-2).</b> The engine exposes no way to
    /// read index entries without reading the records they point at, so this walk also reads the
    /// data pages of everything in the range. An index-only pass would need an accessor on the
    /// connection, which is a second engine change and this plan allows one - so it is written
    /// down here rather than taken.
    /// </summary>
    public ValueSetResult MatchValues(string collectionName, string columnName, IReadOnlyCollection<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var definition = Require(collectionName);
        var column = definition.Column(columnName)
            ?? throw new UnknownColumnException(definition.Name, StorageNames.Normalise(columnName, "column name"));

        var started = Stopwatch.GetTimestamp();
        var before = _connection.PageReadCount;

        var wanted = new List<object>();
        foreach (var value in values)
        {
            if (value is not null && ColumnTypes.TryCanonicalise(column.Type, value, out var canonical)
                                  && canonical is not null)
            {
                wanted.Add(canonical);
            }
        }

        if (wanted.Count == 0)
        {
            return new ValueSetResult([], Info(
                QueryAccess.OrderedPass, definition.Name, column.Name,
                "nothing to look for", 0, 0, 0, Stopwatch.GetElapsedTime(started)));
        }

        var records = (int)Find(definition.Name)!.RecordCount;
        var indexed = HasIndex(definition.Name, column.Name);

        // A large set is worth an index, and building one costs the pass over the collection that
        // answering without one would have cost anyway. Whether an index exists is the engine's
        // business (SC-2) and this is that business being done, not a hint from the schema.
        if (!indexed && wanted.Count >= OrderedPassThreshold)
        {
            _connection.CreateIndex(definition.Name, column.Name);
            indexed = true;
        }

        var seeks = indexed && (long)wanted.Count * SeekRatio <= records;

        var found = seeks
            ? Seek(definition, column, wanted)
            : Walk(definition, column, wanted);

        return new ValueSetResult(found, Info(
            seeks ? QueryAccess.IndexSeek : indexed ? QueryAccess.OrderedPass : QueryAccess.Scan,
            definition.Name,
            column.Name,
            seeks
                ? $"{wanted.Count} seeks of the index on {definition.Name}.{column.Name}"
                : indexed
                    ? $"one ordered walk of the index on {definition.Name}.{column.Name} for {wanted.Count} values"
                    : $"one pass over {definition.Name}, which has no index on {column.Name}",
            records,
            found.Count,
            0,
            Stopwatch.GetElapsedTime(started),
            _connection.PageReadCount - before));
    }

    public int CountNeedingAttention(string collectionName, string columnName)
    {
        var definition = Require(collectionName);
        var column = definition.Column(columnName)
            ?? throw new UnknownColumnException(definition.Name, StorageNames.Normalise(columnName, "column name"));

        return Pending(definition.Name).GetValueOrDefault(column.Name);
    }

    // ---- Structural change (SC-6) ----------------------------------------------------------
    //
    // Rename and remove are the engine's SetColumns, which takes the column set the collection
    // has from now on and the steps a record written before it has to be read through. The steps
    // are the point: the engine is told what changed rather than diffing the two column sets,
    // because a rename and a remove-then-add look identical in a diff and mean opposite things
    // to a record written under the older schema.
    //
    // Nothing here rewrites a record. SetColumns moves the collection's schema version and
    // records the step; the records keep the version they were written under and are read
    // through whatever is above it. Converge is what makes them agree, and it is a separate
    // command because the cost is the caller's to schedule.
    //
    // The indexes are the engine's business and it rebuilds them itself: a unique index built
    // from the old type holds keys a query can no longer match, and there is no lazy version of
    // that.

    public void AddColumn(string collectionName, ColumnDefinition column)
    {
        var before = Require(collectionName);

        // Adding a column changes no stored bytes, so there is no step for a record to be read
        // through - a record that lacks the field simply lacks it. The engine says so itself:
        // "adding a column is not here", in ColumnMigration.
        Apply(before, StructuralChange.Add(before, column), []);
    }

    public void RenameColumn(string collectionName, string columnName, string newName)
    {
        var before = Require(collectionName);
        var after = StructuralChange.Rename(before, columnName, newName);
        if (ReferenceEquals(before, after)) return;

        var from = StorageNames.Normalise(columnName, "column name");
        var to = StorageNames.Normalise(newName, "column name");

        // The pending count follows the column, because the values it counts are the same values.
        var pending = Pending(before.Name);
        if (pending.Remove(from, out var count)) pending[to] = count;

        Apply(before, after, [ColumnMigration.Rename(0, from, to)], pending);
    }

    /// <summary>
    /// Changes what a column's values mean from now on.
    ///
    /// <b>No migration step is recorded with the engine, and that is deliberate.</b> Its
    /// <c>ValueMigration</c> would replay this on read and turn every value it cannot parse into
    /// null, which is the one outcome SC-6b forbids. So the new type is recorded in the
    /// definition and the conversion happens where a value is read - the same laziness, one layer
    /// up, with a different answer for the values that do not fit.
    ///
    /// What that leaves is visible rather than hidden: until <see cref="Converge"/> runs, the
    /// stored values and the index keys over them are still of the old type, so a range over the
    /// column does not reach them. That is what the count kept here is for, and what SC-6c has a
    /// query report.
    /// </summary>
    public void RetypeColumn(string collectionName, string columnName, ColumnType newType)
    {
        var before = Require(collectionName);
        var after = StructuralChange.Retype(before, columnName, newType);
        if (ReferenceEquals(before, after)) return;

        var name = StorageNames.Normalise(columnName, "column name");

        var pending = Pending(before.Name);
        pending[name] = CountNotOfType(before, name, newType);

        Apply(before, after, [], pending);
    }

    public void RemoveColumn(string collectionName, string columnName)
    {
        var before = Require(collectionName);
        var after = StructuralChange.Remove(before, columnName);
        var name = StorageNames.Normalise(columnName, "column name");

        var pending = Pending(before.Name);
        pending.Remove(name);

        Apply(before, after, [ColumnMigration.Remove(0, name)], pending);
    }

    /// <summary>
    /// Brings every record up to the current schema: the engine's own rewrite for the renames
    /// and removals it recorded, and the conversion of the values whose column was retyped.
    ///
    /// Idempotent, and it says what it could not do (SC-6b). A value that has no meaning under
    /// the column's type is left exactly where it is and reported, so running this twice reports
    /// the same set the second time and rewrites nothing.
    /// </summary>
    public ConvergeReport Converge(string collectionName)
    {
        var definition = Require(collectionName);

        // Deliberately not inside a transaction of its own: the engine batches it, because a
        // collection of any size would otherwise hold every one of its pages dirty until the
        // commit. Partial progress is safe - it is the state lazy migration already handles.
        var rewritten = _connection.Rewrite(definition.Name);

        var entities = Entities(definition);
        var stubborn = new List<UnconvertibleValue>();
        var pending = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var record in entities.GetAllRecords().ToList())
        {
            var changed = new Dictionary<string, object?>(record.Value, StorageNames.Comparer);
            var converted = false;
            var left = new List<UnconvertibleValue>();

            foreach (var (column, stored) in EngineRecords.NotOfColumnType(record.Value))
            {
                if (stored.CanConvert)
                {
                    changed[column] = stored.Converted;
                    converted = true;
                    continue;
                }

                left.Add(new UnconvertibleValue(record.RecordId, column, stored.Value, stored.Recorded));
            }

            if (converted)
            {
                try
                {
                    _connection.InTransaction(() => entities.Update(record.RecordId, changed));
                    rewritten++;
                }
                catch (UniqueConstraintViolationException)
                {
                    // Converting it would make it a value another record already holds. SC-6d:
                    // uniqueness in a column of more than one type is per type, so two values
                    // that were distinct as text can be one number - and the honest answer is
                    // the same as for a value that will not parse. It stays, and it is reported.
                    foreach (var (column, stored) in EngineRecords.NotOfColumnType(record.Value))
                    {
                        if (stored.CanConvert)
                        {
                            left.Add(new UnconvertibleValue(
                                record.RecordId, column, stored.Value, stored.Recorded));
                        }
                    }
                }
            }

            foreach (var value in left)
            {
                stubborn.Add(value);
                pending[value.ColumnName] = pending.GetValueOrDefault(value.ColumnName) + 1;
            }
        }

        _connection.SetMetadata(definition.Name, EngineSchema.ToSettings(definition, pending));

        return new ConvergeReport(definition.Name, rewritten, stubborn);
    }

    public RetypeEffect InspectRetype(string collectionName, string columnName, ColumnType newType)
    {
        var definition = Require(collectionName);
        var column = definition.Column(columnName)
            ?? throw new UnknownColumnException(definition.Name, StorageNames.Normalise(columnName, "column name"));

        var inspected = 0;
        var refusing = new List<UnconvertibleValue>();

        foreach (var record in Entities(definition).GetAllRecords())
        {
            if (!record.Value.TryGetValue(column.Name, out var held) || held is null) continue;

            inspected++;

            var recorded = held is StoredAs stored ? stored.Recorded : column.Type;
            var value = held is StoredAs known ? known.Value : held;

            if (!ColumnConversion.TryRetype(recorded, newType, value, out _))
            {
                refusing.Add(new UnconvertibleValue(record.RecordId, column.Name, value, recorded));
            }
        }

        return new RetypeEffect(
            definition.Name,
            column.Name,
            column.Type,
            newType,
            ColumnConversion.IsLossless(column.Type, newType),
            inspected,
            refusing);
    }

    public UniquenessEffect InspectUnique(string collectionName, string columnName)
    {
        var definition = Require(collectionName);
        var column = definition.Column(columnName)
            ?? throw new UnknownColumnException(definition.Name, StorageNames.Normalise(columnName, "column name"));

        var byValue = new Dictionary<object, List<RecordReference>>();
        var inspected = 0;

        foreach (var record in GetAll(definition.Name))
        {
            if (record[column.Name] is not { } value) continue;

            inspected++;

            var key = TextComparison.AsCompared(column.Type, value)!;

            if (!byValue.TryGetValue(key, out var holders))
            {
                holders = [];
                byValue[key] = holders;
            }

            holders.Add(new RecordReference(definition.Name, record.Id));
        }

        return new UniquenessEffect(
            definition.Name,
            column.Name,
            inspected,
            [.. byValue.Values.Where(static holders => holders.Count > 1)]);
    }

    public void SetUnique(string collectionName, string columnName, bool unique)
    {
        var before = Require(collectionName);
        var column = before.Column(columnName)
            ?? throw new UnknownColumnException(before.Name, StorageNames.Normalise(columnName, "column name"));

        if (column.Unique == unique) return;

        if (unique)
        {
            var effect = InspectUnique(before.Name, column.Name);

            if (!effect.IsPossible)
            {
                // Reported as the collisions rather than as the first one the engine would trip
                // over, because IN-6b's whole point is that the caller learns what is in the way
                // before an import starts rather than half way through one.
                throw new StorageValidationException(
                    [.. effect.Collisions.Select(holders => new DuplicateValue(
                        before.Name, column.Name, null, holders[0].Id))]);
            }
        }

        var after = before.WithColumns(
            [.. before.Columns.Select(candidate => StorageNames.Same(candidate.Name, column.Name)
                ? new ColumnDefinition(
                    column.Name, column.Type, column.Purpose, column.Required, unique, column.ReadOnly,
                    column.DefaultValue)
                : candidate)]);

        // A retype step for the column it is on: the engine rebuilds the indexes of every column
        // a step touches, which is what turns the declaration into the unique index that enforces
        // it - and what drops that index again when the declaration goes away.
        Apply(before, after, [ColumnMigration.Retype(0, column.Name, EngineValues.TypeOf(column.Type))]);
    }

    // ---- Relations (SC-8) ---------------------------------------------------------------------

    public void AddRelation(RelationDefinition relation)
    {
        ArgumentNullException.ThrowIfNull(relation);

        var from = Require(relation.FromCollection);
        var to = Require(relation.ToCollection);

        var source = from.Column(relation.FromColumn)
            ?? throw new UnknownColumnException(from.Name, relation.FromColumn);
        var target = to.Column(relation.ToColumn)
            ?? throw new UnknownColumnException(to.Name, relation.ToColumn);

        CheckRelationFits(relation, source, target);

        if (_connection.Relations.Any(existing => existing.Name == relation.Name))
        {
            throw new RelationAlreadyExistsException(relation.Name);
        }

        _connection.CreateRelation(
            relation.Name,
            from.Name,
            source.Name,
            to.Name,
            target.Name,
            // The engine keeps this as a logical property it stores and does not act on, which is
            // exactly what the integrity action is: the engine refuses a reference with no
            // target and says nothing about a delete on the other side, so what a delete does is
            // the assistant's rule and this is where the assistant records it (SC-8).
            relation.Integrity.ToString(),
            relation.Purpose ?? string.Empty);
    }

    public bool RemoveRelation(string relationName) =>
        _connection.RemoveRelation(StorageNames.Normalise(relationName, "relation name"));

    public IReadOnlyCollection<RelationDefinition> GetRelations() =>
        [.. _connection.Relations.Select(ToRelation)];

    // ---- The seams ------------------------------------------------------------------------

    /// <summary>
    /// The column set the collection has from now on, the steps a record written before it is
    /// read through, and the settings that carry what the descriptor has no room for - in one
    /// transaction, so a change cannot half happen.
    /// </summary>
    private void Apply(
        CollectionDefinition before,
        CollectionDefinition after,
        IReadOnlyList<ColumnMigration> steps,
        IReadOnlyDictionary<string, int>? pending = null)
    {
        _connection.InTransaction(() =>
        {
            _connection.SetColumns(after.Name, EngineSchema.ToEngineColumns(after), steps);

            // Rewritten whole rather than patched: SetMetadata replaces the settings document,
            // and the flags in it are derived from the columns, which have just changed.
            _connection.SetMetadata(after.Name, EngineSchema.ToSettings(after, pending ?? Pending(after.Name)));

            if (!Equals(before.DisplayRule, after.DisplayRule))
            {
                _connection.SetDisplayRule(after.Name, after.DisplayRule?.Template ?? string.Empty);
            }
        });
    }

    /// <summary>
    /// What deleting this record would do to the records that refer to it, decided by the
    /// integrity action each relation was created with (SC-8).
    /// </summary>
    private DeletionEffect InspectDelete(CollectionDefinition definition, Ulid id)
    {
        var record = GetById(definition.Name, id);
        if (record is null) return DeletionEffect.Nothing(false);

        var blocking = new List<RecordReference>();
        var removed = new List<RecordReference>();
        var cleared = new List<RecordReference>();

        foreach (var relation in Pointing(definition.Name))
        {
            if (record[relation.ToColumn] is not { } value) continue;

            var referring = ExecuteQuery(new StorageQuery(
                relation.FromCollection,
                [new QueryCondition(relation.FromColumn, QueryOperator.Equals, value)]));

            var references = referring.Records
                .Select(found => new RecordReference(relation.FromCollection, found.Id))
                .ToList();

            if (references.Count == 0) continue;

            switch (relation.Integrity)
            {
                case RelationIntegrity.Cascade:
                    removed.AddRange(references);
                    break;

                case RelationIntegrity.SetEmpty:
                    cleared.AddRange(references);
                    break;

                default:
                    blocking.AddRange(references);
                    break;
            }
        }

        return new DeletionEffect(true, blocking, removed, cleared);
    }

    private RelationDefinition? Refusing(string collectionName, Ulid id)
    {
        var record = GetById(collectionName, id);

        foreach (var relation in Pointing(collectionName))
        {
            if (relation.Integrity is RelationIntegrity.Cascade or RelationIntegrity.SetEmpty) continue;
            if (record?[relation.ToColumn] is not { } value) continue;

            var referring = ExecuteQuery(new StorageQuery(
                relation.FromCollection,
                [new QueryCondition(relation.FromColumn, QueryOperator.Equals, value)]));

            if (referring.Records.Count > 0) return relation;
        }

        return null;
    }

    private IEnumerable<RelationDefinition> From(string collectionName) =>
        GetRelations().Where(relation => StorageNames.Same(relation.FromCollection, collectionName));

    /// <summary>
    /// Whether anything in the collection referred to holds this value. An index seek: the engine
    /// builds the index when the relation is created, precisely because a reference cannot be
    /// checked without one (DC-4).
    /// </summary>
    // ---- Versions (SC-12) -----------------------------------------------------------------------
    //
    // Over DbEntities, the way every other operation here is: the engine keeps the history and
    // answers by version and by moment; this maps its refusals to the contract's own errors, so
    // that a caller sees a DuplicateValue whether the write was an update or a restore.

    public Ulid? HeadVersion(string collectionName, Ulid id)
    {
        var definition = Require(collectionName);

        try
        {
            return Entities(definition).HeadVersion(id);
        }
        catch (RecordNotFoundException)
        {
            // Neither a live image nor a tombstone: the record never existed, or was erased.
            return null;
        }
    }

    public bool Keeps(string collectionName, Ulid id, Ulid versionId)
    {
        var definition = Require(collectionName);

        return Entities(definition).History(id).Versions.Any(version => version.VersionId == versionId);
    }

    public VersionDifference DiffVersions(string collectionName, Ulid id, Ulid fromVersionId, Ulid toVersionId)
    {
        var definition = Require(collectionName);
        var entities = Entities(definition);

        var from = Read(definition, entities, id, fromVersionId);
        var to = Read(definition, entities, id, toVersionId);

        var columns = from.Fields.Keys.Concat(to.Fields.Keys).Distinct(StorageNames.Comparer)
            .OrderBy(static column => column, StringComparer.Ordinal);

        var changes = new List<ColumnChange>();

        foreach (var column in columns)
        {
            var before = from.Fields.GetValueOrDefault(column);
            var after = to.Fields.GetValueOrDefault(column);

            if (!Equals(before, after)) changes.Add(new ColumnChange(column, before, after));
        }

        return new VersionDifference(definition.Name, id, fromVersionId, toVersionId, changes, from.Deleted, to.Deleted);
    }

    /// <summary>One version's values through the current schema, or nothing for a deletion.</summary>
    private static (IReadOnlyDictionary<string, object?> Fields, bool Deleted) Read(
        CollectionDefinition definition, EngineEntities entities, Ulid id, Ulid versionId)
    {
        VersionedValue<Dictionary<string, object?>> version;

        try
        {
            version = entities.GetAsOf(id, versionId);
        }
        catch (VersionNotFoundException)
        {
            throw new VersionNotKeptException(definition.Name, id, versionId);
        }

        if (version.IsDeleted) return (new Dictionary<string, object?>(StorageNames.Comparer), true);

        return (EngineRecords.ToRecord(definition.Name, id, version.Value).Fields, false);
    }

    public StorageRecord RestoreVersion(string collectionName, Ulid id, Ulid versionId)
    {
        var definition = Require(collectionName);
        var entities = Entities(definition);

        var current = entities.GetById(id);
        var before = current is null
            ? []
            : EngineRecords.NotOfColumnType(current.Value).Select(static entry => entry.Column).ToList();

        try
        {
            _connection.InTransaction(() =>
            {
                entities.Restore(id, versionId);

                var restored = entities.GetById(id)!;
                var after = EngineRecords.NotOfColumnType(restored.Value).Select(static entry => entry.Column);
                AdjustPending(definition.Name, Difference(before, after));
            });
        }
        catch (RestoreRefusedException refusal) when (refusal.Reason is RestoreRefusal.CurrentHead)
        {
            // Already there: nothing to write, and nothing to refuse.
        }
        catch (RestoreRefusedException refusal) when (refusal.Reason is RestoreRefusal.NoLongerKept)
        {
            throw new VersionNotKeptException(definition.Name, id, versionId);
        }
        catch (RestoreRefusedException refusal) when (refusal.Reason is RestoreRefusal.Tombstone)
        {
            throw new VersionNotRestorableException(definition.Name, id, versionId);
        }
        catch (UniqueConstraintViolationException duplicate)
        {
            throw new StorageValidationException([
                new DuplicateValue(definition.Name, duplicate.ColumnName,
                    EngineValues.FromDocument(definition.Column(duplicate.ColumnName)?.Type ?? ColumnType.Text,
                        DocumentValues.From(duplicate.Value)),
                    duplicate.ConflictingRecordId)
            ]);
        }
        catch (ReferentialIntegrityException missing)
        {
            var column = definition.Column(missing.Relation.SourceColumn);
            throw new StorageValidationException([
                new ReferenceMissing(definition.Name, missing.Relation.SourceColumn,
                    EngineValues.FromDocument(column?.Type ?? ColumnType.Text, DocumentValues.From(missing.Value)),
                    missing.Relation.Name, missing.Relation.TargetCollection)
            ]);
        }

        var found = entities.GetById(id)!;
        return EngineRecords.ToRecord(definition.Name, id, found.Value);
    }

    public int PurgeRecordHistory(string collectionName, Ulid id, DateTimeOffset before)
    {
        var definition = Require(collectionName);

        return Entities(definition).PurgeHistory(id, before).NodesRemoved;
    }

    public bool Erase(string collectionName, Ulid id)
    {
        var definition = Require(collectionName);
        var entities = Entities(definition);

        var current = entities.GetById(id);
        var pending = current is null
            ? []
            : EngineRecords.NotOfColumnType(current.Value).Select(static entry => entry.Column).ToList();

        try
        {
            _connection.InTransaction(() =>
            {
                entities.Erase(id);

                if (pending.Count > 0)
                {
                    AdjustPending(definition.Name, pending.ToDictionary(static column => column, static _ => -1));
                }
            });
        }
        catch (RecordNotFoundException)
        {
            return false;
        }

        return true;
    }

    private bool Exists(RelationDefinition relation, object value)
    {
        var target = Require(relation.ToCollection);
        var column = target.Column(relation.ToColumn);

        return column is not null
               && Entities(target).GetBy(column.Name, EngineValues.ToDocument(column.Type, value)).Any();
    }

    private IEnumerable<RelationDefinition> Pointing(string collectionName) =>
        GetRelations().Where(relation => StorageNames.Same(relation.ToCollection, collectionName));

    private RelationDefinition? Relation(string relationName) =>
        GetRelations().FirstOrDefault(relation => StorageNames.Same(relation.Name, relationName));

    private static void CheckRelationFits(
        RelationDefinition relation,
        ColumnDefinition source,
        ColumnDefinition target)
    {
        if (!target.Unique)
        {
            throw new InvalidDefinitionException(
                "relation",
                $"'{relation.Name}' refers to '{relation.ToCollection}.{relation.ToColumn}', which two " +
                "records can both hold - so a reference to it would refer to nothing in particular.");
        }

        if (source.Type != target.Type)
        {
            throw new InvalidDefinitionException(
                "relation",
                $"'{relation.Name}' refers from a {source.Type} to a {target.Type}, and one cannot hold " +
                "the other.");
        }

        // SC-8: refused when the relation is created, not when the first delete discovers it.
        // The two rules cannot both hold, and the delete is the wrong moment to find out.
        if (relation.Integrity is RelationIntegrity.SetEmpty && source.Required)
        {
            throw new InvalidDefinitionException(
                "relation",
                $"'{relation.Name}' says to empty '{relation.FromColumn}' when what it refers to goes, and " +
                $"'{relation.FromColumn}' has to have a value.");
        }
    }

    private RelationDefinition ToRelation(RelationDescriptor descriptor) =>
        new(descriptor.Name,
            descriptor.SourceCollection,
            descriptor.SourceColumn,
            descriptor.TargetCollection,
            descriptor.TargetColumn,
            Enum.TryParse<RelationIntegrity>(descriptor.Cardinality, out var integrity)
                ? integrity
                : RelationIntegrity.Restrict,
            string.IsNullOrEmpty(descriptor.Description) ? null : descriptor.Description);

    private bool Remove(CollectionDefinition definition, Ulid id)
    {
        var entities = Entities(definition);
        var found = entities.GetById(id);
        if (found is null) return false;

        var pending = EngineRecords.NotOfColumnType(found.Value).Select(static entry => entry.Column).ToList();

        try
        {
            entities.Delete(id);
        }
        catch (RecordNotFoundException)
        {
            // The engine reports a missing record by throwing; the contract reports it by
            // returning false, because deleting something that is not there is not a failure.
            return false;
        }

        if (pending.Count > 0)
        {
            AdjustPending(definition.Name, pending.ToDictionary(static column => column, static _ => -1));
        }

        return true;
    }

    /// <summary>The referring record, with its reference emptied (SC-8's set empty).</summary>
    private bool Clear(RecordReference reference, string targetCollection)
    {
        var definition = Require(reference.CollectionName);

        var relation = GetRelations().FirstOrDefault(candidate =>
            StorageNames.Same(candidate.FromCollection, reference.CollectionName)
            && StorageNames.Same(candidate.ToCollection, targetCollection)
            && candidate.Integrity is RelationIntegrity.SetEmpty);

        if (relation is null) return false;

        var record = GetById(definition.Name, reference.Id);
        if (record is null) return false;

        return Update(record.With(relation.FromColumn, null));
    }

    private StorageQuery Bound(StorageQuery query, CollectionDefinition definition)
    {
        if (query.After is not { } cursor) return query;
        if (query.OrderBy.Count == 0 || cursor.SortValues.Count == 0) return query;
        if (query.Aggregates.Count > 0) return query;
        if (cursor.SortValues[0] is not { } value) return query;

        var sort = query.OrderBy[0];
        if (definition.Column(sort.ColumnName) is null) return query;

        return query.Resolved([
            new QueryCondition(
                sort.ColumnName,
                sort.Descending ? QueryOperator.LessOrEqual : QueryOperator.GreaterOrEqual,
                value)
        ]);
    }

    /// <summary>
    /// How many records the answer could not consider because their value in a column it asked
    /// about is not of that column's type yet (SC-6c).
    ///
    /// Where more than one column is involved it is their total, which counts a record twice if
    /// two of its values are waiting. It is an upper bound on what was left out and it is never
    /// zero when something was - which is the property that matters, because the failure being
    /// prevented is an answer that looks complete.
    /// </summary>
    private int Excluded(CollectionDefinition definition, ValidatedQuery validated)
    {
        var pending = Pending(definition.Name);
        if (pending.Count == 0) return 0;

        var asked = validated.Where.Select(static condition => condition.Column.Name)
            .Concat(validated.OrderBy.Select(static entry => entry.Column.Name))
            .Distinct(StorageNames.Comparer);

        return asked.Sum(column => pending.GetValueOrDefault(column));
    }

    private StorageQueryResult Nothing(CollectionDefinition definition, StorageQuery query, string why) =>
        new(definition.Name,
            [],
            Info(QueryAccess.IndexSeek, definition.Name, null, why, 0, 0, 0, TimeSpan.Zero),
            QueryMatching.Aggregate([], QueryValidation.Against(definition, query.Resolved([])).Aggregates));

    private static QueryExecutionInfo Info(
        QueryAccess access,
        string collectionName,
        string? columnName,
        string description,
        int examined,
        int returned,
        int excluded,
        TimeSpan elapsed,
        long pages = 0) =>
        new(access, collectionName, columnName, description, examined, returned, excluded, pages, elapsed);

    // ---- Asking the index ----------------------------------------------------------------------

    private List<ValueMatch> Seek(
        CollectionDefinition definition,
        ColumnDefinition column,
        IReadOnlyList<object> values)
    {
        var entities = Entities(definition);
        var found = new List<ValueMatch>(values.Count);

        foreach (var value in values)
        {
            var holder = entities
                .GetBy(column.Name, EngineValues.ToDocument(column.Type, value))
                .Select(static record => (Ulid?)record.RecordId)
                .FirstOrDefault();

            if (holder is { } id) found.Add(new ValueMatch(value, id));
        }

        return found;
    }

    /// <summary>
    /// The ordered pass: the values in key order, the index walked once from the first of them to
    /// the last.
    /// </summary>
    private List<ValueMatch> Walk(
        CollectionDefinition definition,
        ColumnDefinition column,
        IReadOnlyList<object> values)
    {
        // Sorted the way the index is sorted, by the engine's own encoder, so that the first and
        // the last of them really are the ends of the range.
        var ordered = values
            .Select(value => (Value: value, Key: KeyEncoder.Encode(EngineValues.ToDocument(column.Type, value)).Bytes))
            .OrderBy(static entry => entry.Key, KeyComparer.Instance)
            .ToList();

        var query = new NormalizedQuery(
            [
                new QueryPredicate(
                    column.Name,
                    ComparisonOperator.GreaterOrEqual,
                    EngineValues.TypeOf(column.Type),
                    [EngineValues.ToDocument(column.Type, ordered[0].Value)]),
                new QueryPredicate(
                    column.Name,
                    ComparisonOperator.LessOrEqual,
                    EngineValues.TypeOf(column.Type),
                    [EngineValues.ToDocument(column.Type, ordered[^1].Value)])
            ],
            Residual: null!);

        var wanted = new Dictionary<object, object>();
        foreach (var (value, _) in ordered)
        {
            wanted.TryAdd(TextComparison.AsCompared(column.Type, value)!, value);
        }

        var found = new List<ValueMatch>();

        foreach (var record in Entities(definition).Query(query).Records)
        {
            var held = record.Value.GetValueOrDefault(column.Name);
            if (held is StoredAs stored) held = stored.AsRead;
            if (held is null) continue;

            var key = TextComparison.AsCompared(column.Type, held)!;

            if (wanted.Remove(key, out var asked)) found.Add(new ValueMatch(asked, record.RecordId));
        }

        return found;
    }

    private bool HasIndex(string collectionName, string columnName) =>
        _connection.Indexes.Any(index =>
            index.CollectionName == collectionName && index.ColumnName == columnName);

    /// <summary>
    /// The record already holding a value in a unique column, through the index the engine built
    /// when the column was declared unique. This is the whole of what the contract's shared rules
    /// cannot know for themselves: in memory it is a scan, here it is a seek, and the judgement
    /// either answer feeds is the same one.
    /// </summary>
    private Func<ColumnDefinition, object, Ulid?> HolderOf(CollectionDefinition definition)
    {
        var entities = Entities(definition);

        return (column, value) => entities
            .GetBy(column.Name, EngineValues.ToDocument(column.Type, value))
            .Select(static record => (Ulid?)record.RecordId)
            .FirstOrDefault();
    }

    // ---- Values waiting to be made sense of (SC-6b, SC-6c) --------------------------------------

    private Dictionary<string, int> Pending(string collectionName) =>
        EngineSchema.PendingOf(_connection.Metadata(collectionName));

    private void AdjustPending(string collectionName, IReadOnlyDictionary<string, int> delta)
    {
        if (delta.Count == 0) return;

        var pending = Pending(collectionName);

        foreach (var (column, change) in delta)
        {
            var count = pending.GetValueOrDefault(column) + change;

            if (count > 0) pending[column] = count;
            else pending.Remove(column);
        }

        var definition = GetCollectionDefinition(collectionName)!;
        _connection.SetMetadata(collectionName, EngineSchema.ToSettings(definition, pending));
    }

    private static Dictionary<string, int> Difference(IEnumerable<string> before, IEnumerable<string> after)
    {
        var delta = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var column in before) delta[column] = delta.GetValueOrDefault(column) - 1;
        foreach (var column in after) delta[column] = delta.GetValueOrDefault(column) + 1;

        return delta.Where(static entry => entry.Value != 0)
            .ToDictionary(static entry => entry.Key, static entry => entry.Value);
    }

    private static bool IsOfAnotherType(CollectionDefinition definition, string columnName, object? value) =>
        value is not null
        && definition.Column(columnName) is { } column
        && ColumnTypes.TryRecordedType(value, out var recorded)
        && recorded != column.Type;

    private int CountNotOfType(CollectionDefinition definition, string columnName, ColumnType type)
    {
        var counted = 0;

        foreach (var record in Entities(definition).GetAllRecords())
        {
            if (!record.Value.TryGetValue(columnName, out var held) || held is null) continue;

            var recorded = held is StoredAs stored ? stored.Recorded : definition.Column(columnName)!.Type;

            if (recorded != type) counted++;
        }

        return counted;
    }

    /// <summary>
    /// A new serializer per collection, because it carries the column types and the definition
    /// is the only place they are recorded.
    /// </summary>
    private EngineEntities Entities(CollectionDefinition definition) =>
        _connection.Entities(new FieldMapSerializer(definition), definition.Name);

    private CollectionDefinition Require(string collectionName)
    {
        var name = Name(collectionName);
        return GetCollectionDefinition(name) ?? throw new UnknownCollectionException(name);
    }

    private CollectionDescriptor? Find(string collectionName) =>
        _connection.Collections.FirstOrDefault(descriptor =>
            !descriptor.IsSystem && string.Equals(descriptor.Name, collectionName, StringComparison.Ordinal));

    private CollectionDefinition ToDefinition(CollectionDescriptor descriptor) =>
        EngineSchema.ToDefinition(
            descriptor,
            _connection.Metadata(descriptor.Name),
            _connection.DisplayRule(descriptor.Name));

    /// <summary>
    /// Trims and checks a name the way a definition would, so that a name is the same name here
    /// as it is there, and a name that is not a name is refused rather than reported as a
    /// collection that does not exist.
    /// </summary>
    private static string Name(string collectionName) => new CollectionDefinition(collectionName).Name;

    public void Dispose()
    {
        if (!Borrowed)
        {
            _connection.Dispose();
        }
    }
}
