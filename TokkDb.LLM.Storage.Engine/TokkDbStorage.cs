using TokkDb.Documents.Path.Normalization;
using TokkDb.LLM.Core;
using TokkDb.LLM.Core.Diagnostics;
using TokkDb.Pages;
using TokkDb.Values;
using EngineColumn = TokkDb.Pages.ColumnDescriptor;
using EngineConnection = TokkDb.TokkDbConnection;

namespace TokkDb.LLM.Storage.Engine;

/// <summary>
/// The walking skeleton of Phase 4: enough of <see cref="IStorage"/> against the real engine
/// to find out where the two contracts disagree, and no more. Everything outside the covered
/// subset throws <see cref="NotSupportedException"/> rather than pretending to work.
///
/// Deliberately throwaway — Phase 7 writes the adapter this one exists to inform.
/// </summary>
public sealed class TokkDbStorage : IStorage, IDisposable
{
    private readonly EngineConnection _connection;
    private readonly QueryDiagnosticsReporter? _reporter;

    public TokkDbStorage(string databaseFilePath) : this(databaseFilePath, null)
    {
    }

    /// <summary>
    /// UI-4: given a diagnostics service, every query this storage runs reports the access
    /// path it chose and what it cost. Optional because the engine runs perfectly well
    /// without a host listening, and the measurement must not be something the caller has to
    /// remember to switch on per query.
    /// </summary>
    public TokkDbStorage(string databaseFilePath, IDiagnosticsService? diagnostics)
    {
        _connection = new EngineConnection(databaseFilePath);
        _connection.Load();
        _reporter = diagnostics is null ? null : new QueryDiagnosticsReporter(_connection.Queries, diagnostics);
    }

    public void CreateCollection(CollectionDefinition definition)
    {
        _connection.CreateCollection(
            definition.Name,
            definition.Columns.Select(ToEngineColumn),
            definition.Description ?? string.Empty);
    }

    public CollectionDefinition? GetCollectionDefinition(string collectionName)
    {
        var descriptor = _connection.Collections
            .FirstOrDefault(item => string.Equals(item.Name, collectionName, StringComparison.Ordinal));
        return descriptor is null ? null : ToDefinition(descriptor);
    }

    public IReadOnlyCollection<CollectionDefinition> GetCollectionDefinitions()
    {
        return _connection.Collections
            .Where(descriptor => !descriptor.IsSystem)
            .Select(ToDefinition)
            .ToList();
    }

    public StorageRecord Create(string collectionName, IReadOnlyDictionary<string, object?> fields)
    {
        var recordId = Entities(collectionName).Insert(ToFieldMap(fields));
        return new StorageRecord(recordId, collectionName, fields);
    }

    // Both reads go through the planner rather than round the side of it, so that the access
    // path they take is reported like any other query's. A lookup by id is the primary-index
    // path (identity is not a column, so it arrives as an id list rather than a predicate);
    // asking for everything is honestly a full scan, and says so.
    public StorageRecord? GetById(string collectionName, Ulid id)
    {
        var result = Entities(collectionName).Query(NormalizedQuery.Everything, [id]);
        var record = result.Records.FirstOrDefault();
        return record is null ? null : new StorageRecord(id, collectionName, record.Value);
    }

    public IReadOnlyCollection<StorageRecord> GetAll(string collectionName)
    {
        return Entities(collectionName).Query(NormalizedQuery.Everything).Records
            .Select(record => new StorageRecord(record.RecordId, collectionName, record.Value))
            .ToList();
    }

    public bool Update(StorageRecord record)
    {
        try
        {
            Entities(record.CollectionName).Update(record.Id, ToFieldMap(record.Fields));
            return true;
        }
        catch (RecordNotFoundException)
        {
            // IStorage reports a missing record as false; the engine reports it as an exception.
            return false;
        }
    }

    public bool Delete(string collectionName, Ulid id)
    {
        try
        {
            Entities(collectionName).Delete(id);
            return true;
        }
        catch (RecordNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// DC-5: a query in the one representation there is, run through the planner. The
    /// translation into the engine's expression tree happens here and nowhere else, and the
    /// result carries the report of how the records were reached (UI-4).
    ///
    /// Phase 7's <c>ExecuteQuery</c> is this plus the parts of a StorageQuery that are not a
    /// predicate — ordering, paging and projection — which is why they are carried through
    /// the translation but not acted on here.
    /// </summary>
    public DbQueryResult<Dictionary<string, object?>> RunQuery(StorageQuery query)
    {
        var translated = StorageQueryTranslator.Translate(query);
        return Entities(translated.CollectionName).Query(translated.Normalized, translated.Ids);
    }

    /// <summary>
    /// DC-4. IStorage has no index vocabulary of its own yet — Phase 7 decides whether it
    /// should — so an index is created through the engine underneath. It is here because a
    /// query's access path depends on which indexes exist, and a caller that cannot create
    /// one cannot influence that.
    /// </summary>
    public void CreateIndex(string collectionName, string columnName, bool unique = false)
    {
        _connection.CreateIndex(collectionName, columnName, unique);
    }

    public void Dispose()
    {
        _reporter?.Dispose();
        _connection.Dispose();
    }

    private TokkDb.DbEntities<Dictionary<string, object?>> Entities(string collectionName)
    {
        var definition = GetCollectionDefinition(collectionName)
            ?? throw new InvalidOperationException($"Collection '{collectionName}' does not exist.");
        // A new serializer per call: it carries the column types, and the definition is the
        // only place they are recorded.
        return _connection.Entities(new FieldMapSerializer(definition), collectionName);
    }

    private static Dictionary<string, object?> ToFieldMap(IReadOnlyDictionary<string, object?> fields)
    {
        return new Dictionary<string, object?>(fields, StringComparer.Ordinal);
    }

    private static CollectionDefinition ToDefinition(CollectionDescriptor descriptor)
    {
        // Logical only, per the layering note of D-2: the physical pointers the descriptor
        // also carries stay on the engine side of the boundary.
        return new CollectionDefinition(
            descriptor.Name,
            string.IsNullOrEmpty(descriptor.Description) ? null : descriptor.Description,
            descriptor.Columns.Select(ToColumnDefinition).ToList());
    }

    private static ColumnDefinition ToColumnDefinition(EngineColumn column)
    {
        return new ColumnDefinition(
            column.Name,
            ToColumnType(column.Type),
            string.IsNullOrEmpty(column.Description) ? null : column.Description,
            column.Unique,
            column.ReadOnly);
    }

    private static EngineColumn ToEngineColumn(ColumnDefinition column)
    {
        return new EngineColumn(
            column.Name,
            ToValueType(column.Type),
            column.Description ?? string.Empty,
            column.Unique,
            column.ReadOnly);
    }

    private static ValueTypeEnum ToValueType(ColumnType type) => type switch
    {
        ColumnType.String => ValueTypeEnum.String,
        ColumnType.Boolean => ValueTypeEnum.Boolean,
        ColumnType.Int32 => ValueTypeEnum.Int,
        ColumnType.Int64 => ValueTypeEnum.Long,
        ColumnType.Decimal => ValueTypeEnum.Decimal,
        ColumnType.DateTime => ValueTypeEnum.DateTime,
        ColumnType.Guid => ValueTypeEnum.Guid,
        _ => throw new NotSupportedException($"Column type '{type}' has no engine value type.")
    };

    private static ColumnType ToColumnType(ValueTypeEnum type) => type switch
    {
        ValueTypeEnum.String => ColumnType.String,
        ValueTypeEnum.Boolean => ColumnType.Boolean,
        ValueTypeEnum.Int => ColumnType.Int32,
        ValueTypeEnum.Long => ColumnType.Int64,
        ValueTypeEnum.Decimal => ColumnType.Decimal,
        ValueTypeEnum.DateTime => ColumnType.DateTime,
        ValueTypeEnum.Guid => ColumnType.Guid,
        _ => throw new NotSupportedException($"Engine value type '{type}' has no column type.")
    };

    // Everything below is Phase 7's work. It throws rather than returning an empty result,
    // so a caller that needs it finds out here instead of downstream.

    public bool DeleteCollection(string collectionName) => throw NotInSkeleton();

    public void SetDisplayRule(string collectionName, DisplayRule? displayRule) => throw NotInSkeleton();

    public StorageQueryResult ExecuteQuery(StorageQuery query) => throw NotInSkeleton();

    public void AddColumn(string collectionName, ColumnDefinition column) => throw NotInSkeleton();

    public bool UpdateColumn(string collectionName, string currentColumnName, ColumnDefinition updatedColumn) =>
        throw NotInSkeleton();

    public bool RemoveColumn(string collectionName, string columnName) => throw NotInSkeleton();

    public void AddRelation(RelationDefinition relation) => throw NotInSkeleton();

    public bool RemoveRelation(string relationName) => throw NotInSkeleton();

    public RelationDefinition? GetRelation(string relationName) => throw NotInSkeleton();

    public IReadOnlyCollection<RelationDefinition> GetRelations() => throw NotInSkeleton();

    private static NotSupportedException NotInSkeleton() =>
        new("The Phase 4 walking-skeleton adapter covers collection definitions and record " +
            "create, read, update and delete only.");
}
