using TokkDb.Assistant.Storage;
using TokkDb.Pages;
using TokkDb.Pages.Records;
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
/// engine's settings documents, uniqueness in a unique index, and a unit of work is
/// <c>InTransaction</c>.
///
/// <b>What is not here.</b> No rule about what a value may be, and no identity minted. Both come
/// from <c>SharedRules</c> and the engine, so that this and <c>MemoryStorage</c> cannot answer
/// the same question differently - which is the disagreement §2.2 of the engine plan recorded
/// and the reason one contract suite runs against both.
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
    private readonly EngineConnection _connection;

    /// <summary>Opens or creates the database at <paramref name="databaseFilePath"/>.</summary>
    public TokkDbStorage(string databaseFilePath)
    {
        _connection = new EngineConnection(databaseFilePath);
        _connection.Load();
    }

    /// <summary>Takes a connection someone else opened, and does not close it.</summary>
    public TokkDbStorage(EngineConnection connection, bool ownsConnection = false)
    {
        _connection = connection;
        Borrowed = !ownsConnection;
    }

    private bool Borrowed { get; }

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

    // ---- Records --------------------------------------------------------------------------

    public StorageRecord Create(string collectionName, IReadOnlyDictionary<string, object?> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var definition = Require(collectionName);
        var judged = SharedRules.ForCreate(definition, fields, HolderOf(definition));
        var id = Entities(definition).Insert(judged);

        return new StorageRecord(id, definition.Name, judged);
    }

    public StorageRecord? GetById(string collectionName, Ulid id)
    {
        var definition = Require(collectionName);
        var found = Entities(definition).GetById(id);

        return found is null ? null : new StorageRecord(id, definition.Name, found.Value);
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

        var existing = new StorageRecord(record.Id, definition.Name, found.Value);
        var judged = SharedRules.ForUpdate(definition, record, existing, HolderOf(definition));

        entities.Update(record.Id, judged);
        return true;
    }

    public bool Delete(string collectionName, Ulid id)
    {
        var definition = Require(collectionName);

        try
        {
            Entities(definition).Delete(id);
            return true;
        }
        catch (RecordNotFoundException)
        {
            // The engine reports a missing record by throwing; the contract reports it by
            // returning false, because deleting something that is not there is not a failure.
            return false;
        }
    }

    public IReadOnlyCollection<StorageRecord> GetAll(string collectionName)
    {
        var definition = Require(collectionName);

        return Entities(definition).GetAllRecords()
            .Select(record => new StorageRecord(record.RecordId, definition.Name, record.Value))
            .ToList();
    }

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
        var validated = QueryValidation.Against(definition, query);

        var normalized = EngineQuery.ToNormalized(validated);
        // Same story as the residual: the engine takes a null id list to mean "no restriction on
        // identity" while declaring the parameter non-nullable.
        var ids = query.Ids.Count == 0 ? null! : query.Ids;
        var entities = Entities(definition);

        var plan = entities.Explain(normalized, ids);
        var result = entities.Query(normalized, ids);

        var matched = result.Records
            .Select(record => new StorageRecord(record.RecordId, definition.Name, record.Value));

        var page = QueryMatching.Ordered(matched, validated.OrderBy).Skip(query.Skip);
        if (query.Take is { } take) page = page.Take(take);

        var records = page.ToList();

        return new StorageQueryResult(
            definition.Name,
            records,
            EngineQuery.ToAccessPath(plan.Path),
            new QueryCost(
                result.Report.RecordsExamined,
                records.Count,
                result.Report.PagesRead,
                result.Report.Elapsed));
    }

    // ---- Structural change (SC-6) ----------------------------------------------------------
    //
    // All four are the engine's SetColumns, which takes the column set the collection has from
    // now on and the steps a record written before it has to be read through. The steps are the
    // point: the engine is told what changed rather than diffing the two column sets, because a
    // rename and a remove-then-add look identical in a diff and mean opposite things to a
    // record written under the older schema.
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

        Apply(before, after, [ColumnMigration.Rename(0, from, to)]);
    }

    public void RetypeColumn(string collectionName, string columnName, ColumnType newType)
    {
        var before = Require(collectionName);
        var after = StructuralChange.Retype(before, columnName, newType);
        if (ReferenceEquals(before, after)) return;

        var name = StorageNames.Normalise(columnName, "column name");

        // Date and Timestamp are one type to the engine, so a step between them asks it to
        // convert nothing - which is right, because the stored value does not change. What
        // changes is how the column says to read it, and that is in the definition.
        Apply(before, after, [ColumnMigration.Retype(0, name, EngineValues.TypeOf(newType))]);
    }

    public void RemoveColumn(string collectionName, string columnName)
    {
        var before = Require(collectionName);
        var after = StructuralChange.Remove(before, columnName);
        var name = StorageNames.Normalise(columnName, "column name");

        Apply(before, after, [ColumnMigration.Remove(0, name)]);
    }

    public int Converge(string collectionName)
    {
        var definition = Require(collectionName);

        // Deliberately not inside a transaction of its own: the engine batches it, because a
        // collection of any size would otherwise hold every one of its pages dirty until the
        // commit. Partial progress is safe - it is the state lazy migration already handles.
        return _connection.Rewrite(definition.Name);
    }

    /// <summary>
    /// The column set the collection has from now on, the steps a record written before it is
    /// read through, and the settings that carry what the descriptor has no room for - in one
    /// transaction, so a change cannot half happen.
    /// </summary>
    private void Apply(
        CollectionDefinition before,
        CollectionDefinition after,
        IReadOnlyList<ColumnMigration> steps)
    {
        _connection.InTransaction(() =>
        {
            _connection.SetColumns(after.Name, EngineSchema.ToEngineColumns(after), steps);

            // Rewritten whole rather than patched: SetMetadata replaces the settings document,
            // and the flags in it are derived from the columns, which have just changed.
            _connection.SetMetadata(after.Name, EngineSchema.ToSettings(after));

            if (!Equals(before.DisplayRule, after.DisplayRule))
            {
                _connection.SetDisplayRule(after.Name, after.DisplayRule?.Template ?? string.Empty);
            }
        });
    }

    public void Dispose()
    {
        if (!Borrowed)
        {
            _connection.Dispose();
        }
    }

    // ---- The seams ------------------------------------------------------------------------

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
}
