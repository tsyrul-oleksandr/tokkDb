using System.Globalization;
using TokkDb.Documents.Path.Normalization;
using TokkDb.LLM.Core;
using TokkDb.LLM.Core.Diagnostics;
using TokkDb.Pages;
using TokkDb.Values;
using EngineColumn = TokkDb.Pages.ColumnDescriptor;
using TokkDb.Documents;
using TokkDb.Documents.Values;
using TokkDb.Pages.Query;
using TokkDb.Pages.Relations;
using EngineConnection = TokkDb.TokkDbConnection;

namespace TokkDb.LLM.Storage.Engine;

/// <summary>
/// <see cref="IStorage"/> against the real engine, in full. It replaces the Phase 4 walking
/// skeleton, which implemented collection definitions and record CRUD and threw for
/// everything else; what that skeleton was for was finding out where the two contracts
/// disagree before this was written, and section 2.2 records what it found.
///
/// The division of labour is D-2's. The engine owns storage — pages, indexes, transactions,
/// the access path a query takes — and knows nothing of a <c>ColumnDefinition</c>. This owns
/// the logical schema: what a cardinality requires of a column, what a display rule may
/// refer to, which columns a query may name. A <see cref="CollectionDefinition"/> that leaves
/// here carries the five logical things and none of the physical pointers the catalogue
/// document also holds, so the agent tools cannot see a page number.
///
/// Where the two backends still differ, the difference is a capability property of the shared
/// contract tests rather than a habit either of them has quietly acquired.
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
        ArgumentNullException.ThrowIfNull(definition);
        _connection.CreateCollection(
            definition.Name,
            definition.Columns.Select(ToEngineColumn),
            definition.Description ?? string.Empty);
        // Both are the collection's, and neither is structural (D-4): they are documents in
        // _settings and _displayRules, so changing one later does not rewrite the descriptor
        // the data pages and index roots live in.
        if (definition.Metadata.Count > 0)
        {
            _connection.SetMetadata(definition.Name, definition.Metadata
                .ToDictionary(entry => entry.Key, entry => entry.Value ?? string.Empty, StringComparer.Ordinal));
        }

        if (definition.DisplayRule is not null)
        {
            SetDisplayRule(definition.Name, definition.DisplayRule);
        }
    }

    public CollectionDefinition? GetCollectionDefinition(string collectionName)
    {
        var descriptor = _connection.Collections
            .FirstOrDefault(item => string.Equals(item.Name, collectionName, StringComparison.Ordinal));
        return descriptor is null ? null : ToDefinition(descriptor);
    }

    /// <summary>
    /// D-4: the metadata the application keeps about a collection, stored as its own document
    /// rather than as part of the schema. It is a dictionary of strings to the engine, which
    /// has no opinion about what any of it means.
    /// </summary>
    public void SetMetadata(string collectionName, IReadOnlyDictionary<string, string?> metadata)
    {
        collectionName = StorageValidation.NormalizeName(collectionName, "collection name");
        RequireDefinition(collectionName);
        _connection.SetMetadata(collectionName, (metadata ?? new Dictionary<string, string?>())
            .ToDictionary(entry => entry.Key, entry => entry.Value ?? string.Empty, StringComparer.Ordinal));
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
    /// <see cref="ExecuteQuery"/> is this plus the parts of a StorageQuery that are not a
    /// predicate — ordering, paging and projection — which is why they are carried through
    /// the translation but not acted on here. A caller that wants the access path and what it
    /// cost uses this; one that wants the IStorage answer uses ExecuteQuery.
    /// </summary>
    public DbQueryResult<Dictionary<string, object?>> RunQuery(StorageQuery query)
    {
        var translated = StorageQueryTranslator.Translate(query);
        return Entities(translated.CollectionName).Query(translated.Normalized, translated.Ids);
    }

    /// <summary>
    /// DC-4. IStorage has no index vocabulary of its own, so an index over a non-unique column
    /// is created through the engine underneath. A unique column indexes itself: the
    /// uniqueness is enforced by that index, so declaring the column is what creates it.
    /// This is here because a query's access path depends on which indexes exist, and a caller
    /// that cannot create one cannot influence that.
    /// </summary>
    public void CreateIndex(string collectionName, string columnName, bool unique = false)
    {
        _connection.CreateIndex(collectionName, columnName, unique);
    }

    /// <summary>
    /// UI-4: the engine's query reports, for a host that wants them without going through a
    /// diagnostics service — the benchmarks and the tests that assert an access path.
    /// </summary>
    public QueryService Queries => _connection.Queries;

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

    /// <summary>
    /// D-2: logical only. Name, description, columns, metadata and display rule — the five
    /// things the agent tools reason about. The physical pointers the descriptor also carries
    /// (the data chain, the index roots, the free-space root, the record count) stay on the
    /// engine side of the boundary and have no member here to leak through.
    /// </summary>
    private CollectionDefinition ToDefinition(CollectionDescriptor descriptor)
    {
        return new CollectionDefinition(
            descriptor.Name,
            string.IsNullOrEmpty(descriptor.Description) ? null : descriptor.Description,
            descriptor.Columns.Select(ToColumnDefinition).ToList(),
            _connection.Metadata(descriptor.Name)
                .ToDictionary(entry => entry.Key, entry => (string?)entry.Value, StringComparer.Ordinal),
            DisplayRule.TryCreate(_connection.DisplayRule(descriptor.Name)));
    }

    private static ColumnDefinition ToColumnDefinition(EngineColumn column)
    {
        var type = ToColumnType(column.Type);
        return new ColumnDefinition(
            column.Name,
            type,
            string.IsNullOrEmpty(column.Description) ? null : column.Description,
            column.Unique,
            column.ReadOnly,
            FromDefaultValue(column.DefaultValue, type),
            column.SemanticTypeName,
            validationPatterns: column.ValidationPatterns);
    }

    private static EngineColumn ToEngineColumn(ColumnDefinition column)
    {
        return new EngineColumn(
            column.Name,
            ToValueType(column.Type),
            column.Description ?? string.Empty,
            column.Unique,
            column.ReadOnly,
            ToDefaultValue(column.DefaultValue),
            column.SemanticTypeName ?? string.Empty,
            column.ValidationPatterns);
    }

    /// <summary>
    /// The declared default, stored the way a value of that column is stored — which for the
    /// four types the document format has no value for means invariant text, exactly as
    /// <see cref="FieldMapSerializer"/> writes them.
    /// </summary>
    private static IDocumentValue ToDefaultValue(object? value) => value switch
    {
        null => new NullDocumentValue(),
        string text => new StringDocumentValue(text),
        bool flag => new BooleanDocumentValue(flag),
        int number => new IntDocumentValue(number),
        long number => new StringDocumentValue(number.ToString(CultureInfo.InvariantCulture)),
        decimal number => new StringDocumentValue(number.ToString(CultureInfo.InvariantCulture)),
        DateTime moment => new StringDocumentValue(moment.ToString("O", CultureInfo.InvariantCulture)),
        Guid id => new StringDocumentValue(id.ToString("D")),
        _ => new NullDocumentValue()
    };

    private static object? FromDefaultValue(IDocumentValue? value, ColumnType type) => value switch
    {
        null or NullDocumentValue => null,
        BooleanDocumentValue flag => flag.Value,
        IntDocumentValue number => number.Value,
        StringDocumentValue text => type switch
        {
            ColumnType.Int64 => long.TryParse(text.Value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var number) ? number : null,
            ColumnType.Decimal => decimal.TryParse(text.Value, NumberStyles.Number, CultureInfo.InvariantCulture,
                out var number) ? number : null,
            ColumnType.DateTime => DateTime.TryParse(text.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var moment) ? moment : null,
            ColumnType.Guid => Guid.TryParse(text.Value, out var id) ? id : null,
            _ => text.Value
        },
        _ => null
    };

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

    // ---- schema: collections ----

    /// <summary>
    /// Removes a collection and everything the engine holds about it, in one transaction
    /// (DC-8). Refused while a relation names it, because dropping it would leave a
    /// constraint pointing at nothing.
    /// </summary>
    public bool DeleteCollection(string collectionName)
    {
        collectionName = StorageValidation.NormalizeName(collectionName, "collection name");
        if (_connection.Relations.Any(relation =>
                relation.SourceCollection == collectionName || relation.TargetCollection == collectionName))
        {
            throw new InvalidOperationException($"Collection '{collectionName}' is used by one or more relations.");
        }

        return _connection.DropCollection(collectionName);
    }

    /// <summary>
    /// D-4: the rule is a document in <c>_displayRules</c>, not a field of the structural
    /// descriptor. It is validated against the current schema before it is stored — a rule
    /// naming a column that does not exist renders nothing, and finding that out at render
    /// time tells the caller nothing about which of its rules is wrong.
    /// </summary>
    public void SetDisplayRule(string collectionName, DisplayRule? displayRule)
    {
        collectionName = StorageValidation.NormalizeName(collectionName, "collection name");
        var definition = RequireDefinition(collectionName);

        if (displayRule is null)
        {
            _connection.SetDisplayRule(collectionName, null);
            return;
        }

        var compiled = CompiledDisplayRule.Parse(displayRule.Template);
        if (!compiled.IsValidSyntax)
        {
            throw new InvalidOperationException($"Display rule is invalid: {compiled.ParseError}");
        }

        var missing = compiled.ColumnReferences
            .Where(reference => !definition.Columns.Any(column =>
                string.Equals(column.Name, reference, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Display rule references unknown column(s): {string.Join(", ", missing)}.");
        }

        _connection.SetDisplayRule(collectionName, displayRule.Template);
    }

    // ---- schema: columns ----

    /// <summary>
    /// DC-7. A column is added by replacing the collection's column set, which bumps the
    /// schema version. Records already stored are not rewritten: they carry the version they
    /// were written under (VR-11) and simply have no value for the new column, which is what
    /// makes the migration lazy rather than a rewrite of the whole collection.
    /// </summary>
    public void AddColumn(string collectionName, ColumnDefinition column)
    {
        collectionName = StorageValidation.NormalizeName(collectionName, "collection name");
        ArgumentNullException.ThrowIfNull(column);

        var columns = RequireDefinition(collectionName).Columns.ToList();
        if (columns.Any(existing => string.Equals(existing.Name, column.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Column '{column.Name}' already exists in '{collectionName}'.");
        }

        columns.Add(column);
        SetColumns(collectionName, columns);
    }

    /// <summary>
    /// Replaces one column, possibly renaming it. A rename carries the stored values across
    /// and rewrites the relations and the display rule that named the old column, so nothing
    /// is left pointing at a name that no longer exists.
    /// </summary>
    public bool UpdateColumn(string collectionName, string currentColumnName, ColumnDefinition updatedColumn)
    {
        collectionName = StorageValidation.NormalizeName(collectionName, "collection name");
        currentColumnName = StorageValidation.NormalizeName(currentColumnName, "column name");
        ArgumentNullException.ThrowIfNull(updatedColumn);

        var definition = RequireDefinition(collectionName);
        var existing = definition.Columns.FirstOrDefault(column =>
            string.Equals(column.Name, currentColumnName, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return false;
        }

        var renamed = !string.Equals(existing.Name, updatedColumn.Name, StringComparison.OrdinalIgnoreCase);
        if (renamed && definition.Columns.Any(column =>
                string.Equals(column.Name, updatedColumn.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Column '{updatedColumn.Name}' already exists in '{collectionName}'.");
        }

        var columns = definition.Columns
            .Select(column => column == existing ? updatedColumn : column)
            .ToList();

        // The values move before the schema does. Reading them needs the old column set to
        // decode with, and after the change that set is gone.
        var rewritten = renamed || updatedColumn.Type != existing.Type
            ? ReadAllFields(collectionName, definition)
            : null;

        SetColumns(collectionName, columns);

        if (rewritten is not null)
        {
            RewriteRecords(collectionName, rewritten, existing.Name, updatedColumn);
        }

        if (renamed)
        {
            RenameInRelations(collectionName, existing.Name, updatedColumn.Name);
            RenameInDisplayRule(collectionName, existing.Name, updatedColumn.Name);
        }

        return true;
    }

    /// <summary>
    /// Removes a column and the values stored under it. Refused while a relation names it,
    /// for the same reason a collection cannot be dropped out from under one.
    /// </summary>
    public bool RemoveColumn(string collectionName, string columnName)
    {
        collectionName = StorageValidation.NormalizeName(collectionName, "collection name");
        columnName = StorageValidation.NormalizeName(columnName, "column name");

        var definition = RequireDefinition(collectionName);
        if (!definition.Columns.Any(column =>
                string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (_connection.Relations.Any(relation =>
                (relation.SourceCollection == collectionName && relation.SourceColumn == columnName) ||
                (relation.TargetCollection == collectionName && relation.TargetColumn == columnName)))
        {
            throw new InvalidOperationException($"Column '{columnName}' is used by one or more relations.");
        }

        var stored = ReadAllFields(collectionName, definition);
        SetColumns(collectionName, definition.Columns
            .Where(column => !string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase))
            .ToList());
        RewriteRecords(collectionName, stored, columnName, null);
        return true;
    }

    // ---- schema: relations ----

    /// <summary>
    /// DC-4. Adding a relation creates the index on its target column if the column has none,
    /// because the referential check is a lookup by value and there is nothing else it could
    /// be. The cardinality rules are checked here rather than in the engine: what OneToOne
    /// requires of a column is a statement about the schema, and the engine's check is the
    /// same whatever the cardinality says.
    /// </summary>
    public void AddRelation(RelationDefinition relation)
    {
        ArgumentNullException.ThrowIfNull(relation);
        if (_connection.Relations.Any(existing => existing.Name == relation.Name))
        {
            throw new InvalidOperationException($"Relation '{relation.Name}' already exists.");
        }

        ValidateRelationDefinition(relation);
        _connection.CreateRelation(
            relation.Name,
            relation.SourceCollection,
            relation.SourceColumn,
            relation.TargetCollection,
            relation.TargetColumn,
            relation.Type.ToString(),
            relation.Description ?? string.Empty);
        BumpSchemaVersionsForRelation(relation);
    }

    public bool RemoveRelation(string relationName)
    {
        relationName = StorageValidation.NormalizeName(relationName, "relation name");
        var relation = GetRelation(relationName);
        if (relation is null)
        {
            return false;
        }

        var removed = _connection.RemoveRelation(relationName);
        if (removed)
        {
            BumpSchemaVersionsForRelation(relation);
        }

        return removed;
    }

    public RelationDefinition? GetRelation(string relationName)
    {
        relationName = StorageValidation.NormalizeName(relationName, "relation name");
        return _connection.Relations
            .Where(relation => relation.Name == relationName)
            .Select(ToRelationDefinition)
            .FirstOrDefault();
    }

    public IReadOnlyCollection<RelationDefinition> GetRelations()
    {
        return _connection.Relations.Select(ToRelationDefinition).ToArray();
    }

    // ---- queries ----

    /// <summary>
    /// DC-5. The query is checked against the schema, translated into the engine's expression
    /// tree, planned against the indexes that exist, and run. Ordering, paging and projection
    /// happen here rather than in the planner: they shape the answer rather than decide which
    /// records are read, and the engine has no vocabulary for a column definition.
    /// </summary>
    public StorageQueryResult ExecuteQuery(StorageQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        StorageQueryValidator.ThrowIfInvalid(query);

        var matched = RunQuery(query).Records;
        var rows = ApplyOrdering(matched, query.OrderBy)
            .Skip(query.Skip)
            .Take(query.Take)
            .Select(record => Project(record, query.Select))
            .ToArray();

        return new StorageQueryResult(query.Collection.Name, rows, query.Skip, query.Take);
    }

    // ---- helpers ----

    private CollectionDefinition RequireDefinition(string collectionName)
    {
        return GetCollectionDefinition(collectionName)
            ?? throw new InvalidOperationException($"Collection '{collectionName}' does not exist.");
    }

    private void SetColumns(string collectionName, IReadOnlyCollection<ColumnDefinition> columns)
    {
        _connection.SetColumns(collectionName, columns.Select(ToEngineColumn));
    }

    /// <summary>
    /// Every record of the collection, decoded with the schema as it is now. Taken before a
    /// column change so the values can be written back under the new one — the engine stores
    /// four of the seven column types as text and the column definition is what says which
    /// type that text stands for, so a record read after the change would be read wrongly.
    /// </summary>
    private List<StorageRecord> ReadAllFields(string collectionName, CollectionDefinition definition)
    {
        return _connection.Entities(new FieldMapSerializer(definition), collectionName)
            .GetAllRecords()
            .Select(record => new StorageRecord(record.RecordId, collectionName, record.Value))
            .ToList();
    }

    /// <summary>
    /// Writes the records back with one column renamed, retyped or dropped. A rename keeps
    /// the value under its new name; <paramref name="updatedColumn"/> of null drops it.
    /// </summary>
    private void RewriteRecords(
        string collectionName,
        List<StorageRecord> records,
        string previousColumnName,
        ColumnDefinition? updatedColumn)
    {
        if (records.Count == 0)
        {
            return;
        }

        var entities = Entities(collectionName);
        // One transaction for the lot: a half-migrated collection is a schema the definition
        // no longer describes, which nothing downstream could read.
        _connection.InTransaction(() =>
        {
            foreach (var record in records)
            {
                var fields = new Dictionary<string, object?>(record.Fields, StringComparer.Ordinal);
                if (!fields.Remove(previousColumnName, out var value))
                {
                    continue;
                }

                if (updatedColumn is not null)
                {
                    fields[updatedColumn.Name] = Convert(value, updatedColumn.Type);
                }

                entities.Update(record.Id, fields);
            }
        });
    }

    /// <summary>
    /// A stored value moved to a column of a different type. A value that cannot be converted
    /// is dropped rather than kept: keeping it would store text under a column whose type
    /// says it is a number, and every later read of the record would throw.
    /// </summary>
    private static object? Convert(object? value, ColumnType type)
    {
        if (value is null || StorageValidation.IsValueCompatible(type, value))
        {
            return value;
        }

        var text = value.ToString();
        if (text is null)
        {
            return null;
        }

        return type switch
        {
            ColumnType.String => text,
            ColumnType.Boolean => bool.TryParse(text, out var flag) ? flag : null,
            ColumnType.Int32 => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                ? i : null,
            ColumnType.Int64 => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
                ? l : null,
            ColumnType.Decimal => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
                ? d : null,
            ColumnType.DateTime => DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var moment) ? moment : null,
            ColumnType.Guid => Guid.TryParse(text, out var id) ? id : null,
            _ => null
        };
    }

    // A relation naming the old column would otherwise describe a constraint on a column that
    // no longer exists. The relation is recreated rather than edited: its descriptor is one
    // document and the engine has no partial update for it.
    private void RenameInRelations(string collectionName, string previousColumnName, string newColumnName)
    {
        foreach (var relation in GetRelations())
        {
            var source = relation.SourceCollection == collectionName && relation.SourceColumn == previousColumnName;
            var target = relation.TargetCollection == collectionName && relation.TargetColumn == previousColumnName;
            if (!source && !target)
            {
                continue;
            }

            _connection.RemoveRelation(relation.Name);
            _connection.CreateRelation(
                relation.Name,
                relation.SourceCollection,
                source ? newColumnName : relation.SourceColumn,
                relation.TargetCollection,
                target ? newColumnName : relation.TargetColumn,
                relation.Type.ToString(),
                relation.Description ?? string.Empty);
        }
    }

    // Only genuine {Column} references are rewritten — literal text that happens to contain
    // the old name is left alone, which is why this goes through the parsed template.
    private void RenameInDisplayRule(string collectionName, string previousColumnName, string newColumnName)
    {
        if (_connection.DisplayRule(collectionName) is not { } template)
        {
            return;
        }

        var compiled = CompiledDisplayRule.Parse(template);
        if (!compiled.IsValidSyntax)
        {
            return;
        }

        _connection.SetDisplayRule(collectionName,
            compiled.RewriteColumnReference(previousColumnName, newColumnName));
    }

    /// <summary>
    /// A relation changes what a record of both collections means, so both schema versions
    /// move. The version is what a lazily migrating reader compares against (DC-7).
    /// </summary>
    private void BumpSchemaVersionsForRelation(RelationDefinition relation)
    {
        foreach (var collectionName in new[] { relation.SourceCollection, relation.TargetCollection }.Distinct())
        {
            if (GetCollectionDefinition(collectionName) is { } definition)
            {
                SetColumns(collectionName, definition.Columns);
            }
        }
    }

    private void ValidateRelationDefinition(RelationDefinition relation)
    {
        var source = RequireDefinition(relation.SourceCollection);
        var target = RequireDefinition(relation.TargetCollection);

        var sourceColumn = source.Columns.FirstOrDefault(column => column.Name == relation.SourceColumn)
            ?? throw new InvalidOperationException(
                $"Relation '{relation.Name}' references missing source column '{relation.SourceColumn}'.");
        var targetColumn = target.Columns.FirstOrDefault(column => column.Name == relation.TargetColumn)
            ?? throw new InvalidOperationException(
                $"Relation '{relation.Name}' references missing target column '{relation.TargetColumn}'.");

        if (sourceColumn.Type != targetColumn.Type)
        {
            throw new InvalidOperationException(
                $"Relation '{relation.Name}' columns '{relation.SourceColumn}' and '{relation.TargetColumn}' " +
                "have incompatible types.");
        }

        if (relation.Type is RelationType.OneToOne && (!sourceColumn.Unique || !targetColumn.Unique))
        {
            throw new InvalidOperationException(
                $"Relation '{relation.Name}' of type OneToOne requires both columns to be unique or primary keys.");
        }

        if (relation.Type is RelationType.ManyToOne && !targetColumn.Unique)
        {
            throw new InvalidOperationException(
                $"Relation '{relation.Name}' of type ManyToOne requires target column " +
                $"'{relation.TargetColumn}' to be unique or primary key.");
        }

        if (relation.Type is RelationType.OneToMany && !sourceColumn.Unique)
        {
            throw new InvalidOperationException(
                $"Relation '{relation.Name}' of type OneToMany requires source column " +
                $"'{relation.SourceColumn}' to be unique or primary key.");
        }
    }

    private static RelationDefinition ToRelationDefinition(RelationDescriptor descriptor)
    {
        return new RelationDefinition(
            descriptor.Name,
            Enum.TryParse<RelationType>(descriptor.Cardinality, out var type) ? type : RelationType.ManyToOne,
            descriptor.SourceCollection,
            descriptor.SourceColumn,
            descriptor.TargetCollection,
            descriptor.TargetColumn,
            string.IsNullOrEmpty(descriptor.Description) ? null : descriptor.Description);
    }

    private static IEnumerable<DbRecord<Dictionary<string, object?>>> ApplyOrdering(
        IReadOnlyList<DbRecord<Dictionary<string, object?>>> records,
        IReadOnlyList<StorageSort> orderBy)
    {
        if (orderBy.Count == 0)
        {
            return records;
        }

        var comparer = Comparer<object?>.Create(CompareValues);
        IOrderedEnumerable<DbRecord<Dictionary<string, object?>>>? ordered = null;
        foreach (var sort in orderBy)
        {
            var columnName = sort.Column.Name;
            object? Key(DbRecord<Dictionary<string, object?>> record) =>
                record.Value.GetValueOrDefault(columnName);

            ordered = ordered is null
                ? sort.Descending ? records.OrderByDescending(Key, comparer) : records.OrderBy(Key, comparer)
                : sort.Descending ? ordered.ThenByDescending(Key, comparer) : ordered.ThenBy(Key, comparer);
        }

        return ordered!;
    }

    // Nulls sort first and numbers compare as numbers whatever CLR type they arrived as, so
    // an Int32 column and an Int64 one order the same way.
    private static int CompareValues(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null ? 0 : left is null ? -1 : 1;
        }

        if (TryAsDecimal(left, out var leftNumber) && TryAsDecimal(right, out var rightNumber))
        {
            return leftNumber.CompareTo(rightNumber);
        }

        if (left is IComparable comparable && left.GetType() == right.GetType())
        {
            return comparable.CompareTo(right);
        }

        return string.Compare(left.ToString(), right.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryAsDecimal(object value, out decimal result)
    {
        switch (value)
        {
            case decimal number: result = number; return true;
            case int number: result = number; return true;
            case long number: result = number; return true;
            case double number: result = (decimal)number; return true;
            default: result = 0; return false;
        }
    }

    private static StorageQueryRow Project(
        DbRecord<Dictionary<string, object?>> record,
        IReadOnlyList<ColumnDefinition> select)
    {
        var names = select.Count == 0
            ? record.Value.Keys.ToArray()
            : select.Select(column => column.Name).ToArray();

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in names.Where(record.Value.ContainsKey))
        {
            fields[name] = record.Value[name];
        }

        return new StorageQueryRow(record.RecordId, fields);
    }
}
