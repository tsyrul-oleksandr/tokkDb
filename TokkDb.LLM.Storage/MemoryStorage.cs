using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using TokkDb.LLM.Core;

namespace TokkDb.LLM.Storage;

public sealed partial class MemoryStorage : IStorage
{
    public const string SchemaVersionMetadataKey = "schemaVersion";
    private readonly Dictionary<string, CollectionState> _collections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RelationDefinition> _relations = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly ISemanticTypeRegistry? _semanticTypeRegistry;
    private readonly ILogger<MemoryStorage> _logger;

    public MemoryStorage(
        ISemanticTypeRegistry? semanticTypeRegistry = null,
        ILogger<MemoryStorage>? logger = null)
    {
        _semanticTypeRegistry = semanticTypeRegistry;
        // Null logger keeps the parameterless construction used by tests working.
        _logger = logger ?? NullLogger<MemoryStorage>.Instance;
    }

    public void CreateCollection(CollectionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        lock (_sync)
        {
            definition = EnsureSchemaVersion(definition, 1);
            ValidateSemanticTypes(definition.Columns);
            if (_collections.ContainsKey(definition.Name))
            {
                _logger.LogWarning(
                    "Collection creation rejected, name already exists. Collection: {CollectionName}",
                    definition.Name);
                throw new InvalidOperationException($"Collection '{definition.Name}' already exists.");
            }

            _collections.Add(definition.Name, new CollectionState(definition));
            _logger.LogInformation(
                "Collection created. Collection: {CollectionName}, Columns: {ColumnCount}",
                definition.Name,
                definition.Columns.Count);
        }
    }

    public bool DeleteCollection(string collectionName)
    {
        collectionName = StorageValidation.NormalizeName(collectionName, "collection name");

        lock (_sync)
        {
            if (_relations.Values.Any(relation =>
                    string.Equals(relation.SourceCollection, collectionName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(relation.TargetCollection, collectionName, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning(
                    "Collection deletion rejected, relations depend on it. Collection: {CollectionName}",
                    collectionName);
                throw new InvalidOperationException($"Collection '{collectionName}' is used by one or more relations.");
            }

            var removed = _collections.Remove(collectionName);
            if (removed)
            {
                _logger.LogInformation("Collection deleted. Collection: {CollectionName}", collectionName);
            }
            else
            {
                _logger.LogDebug(
                    "Collection deletion found nothing to remove. Collection: {CollectionName}",
                    collectionName);
            }

            return removed;
        }
    }

    /// <summary>
    /// Sets or clears the display rule for a collection. The rule is validated
    /// against the current schema before it is applied; an invalid rule is
    /// rejected rather than stored.
    /// </summary>
    public void SetDisplayRule(string collectionName, DisplayRule? displayRule)
    {
        collectionName = StorageValidation.NormalizeName(collectionName, "collection name");

        lock (_sync)
        {
            var state = GetCollectionState(collectionName);

            if (displayRule is null)
            {
                state.DisplayRule = null;
                state.RebuildDefinition();
                _logger.LogInformation("DisplayRule cleared. Collection: {CollectionName}", collectionName);
                return;
            }

            var missing = FindMissingReferences(displayRule, state, out var parseError);
            if (parseError is not null)
            {
                _logger.LogWarning(
                    "DisplayRule rejected, invalid syntax. Collection: {CollectionName}, Error: {ParseError}",
                    collectionName,
                    parseError);
                throw new InvalidOperationException($"Display rule is invalid: {parseError}");
            }

            if (missing.Count > 0)
            {
                _logger.LogWarning(
                    "DisplayRule rejected, references missing columns. Collection: {CollectionName}, Columns: {MissingColumns}",
                    collectionName,
                    string.Join(",", missing));
                throw new InvalidOperationException(
                    $"Display rule references unknown column(s): {string.Join(", ", missing)}.");
            }

            var previous = state.DisplayRule;
            state.DisplayRule = displayRule;
            state.RebuildDefinition();

            _logger.LogInformation(
                "DisplayRule applied. Collection: {CollectionName}, Created: {Created}, Template: {DisplayRuleTemplate}",
                collectionName,
                previous is null,
                displayRule.Template);
        }
    }

    /// <summary>
    /// Rewrites display-rule column references after a rename.
    ///
    /// Only genuine <c>{Column}</c> references are rewritten - literal text that
    /// happens to contain the old name is left alone, which is why this goes
    /// through the parsed template rather than a string replace. The rewritten
    /// rule is validated before it replaces the current one.
    /// </summary>
    private void RepairDisplayRuleAfterRename(
        CollectionState state,
        string collectionName,
        string fromColumn,
        string toColumn)
    {
        var rule = state.DisplayRule;
        if (rule is null)
        {
            return;
        }

        var compiled = CompiledDisplayRule.Parse(rule.Template);
        if (!compiled.IsValidSyntax ||
            !compiled.ColumnReferences.Contains(fromColumn, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var repairedTemplate = compiled.RewriteColumnReference(fromColumn, toColumn);
        var repaired = DisplayRule.TryCreate(repairedTemplate);
        if (repaired is null)
        {
            _logger.LogError(
                "DisplayRule repair produced an unusable template. Collection: {CollectionName}, Column: {ColumnName}, NewName: {NewColumnName}",
                collectionName,
                fromColumn,
                toColumn);
            return;
        }

        // Validate the repair before applying it.
        var missing = FindMissingReferences(repaired, state, out var parseError);
        if (parseError is not null || missing.Count > 0)
        {
            _logger.LogWarning(
                "DisplayRule repair rejected by validation; rule left unchanged and now references a renamed column. Collection: {CollectionName}, Error: {ParseError}, MissingColumns: {MissingColumns}",
                collectionName,
                parseError,
                string.Join(",", missing));
            return;
        }

        state.DisplayRule = repaired;
        // The definition was already rebuilt by the schema-version bump, so it
        // must be rebuilt again to expose the repaired rule.
        state.RebuildDefinition();
        _logger.LogInformation(
            "DisplayRule repaired after column rename. Collection: {CollectionName}, Column: {ColumnName}, NewName: {NewColumnName}, Template: {DisplayRuleTemplate}",
            collectionName,
            fromColumn,
            toColumn,
            repaired.Template);
    }

    /// <summary>
    /// Reports a display rule left invalid by a column removal. The rule is kept
    /// so it can be repaired deliberately; evaluation falls back safely until
    /// then. Removal is ambiguous to auto-repair, so this only diagnoses.
    /// </summary>
    private void ReportDisplayRuleConflictAfterRemoval(
        CollectionState state,
        string collectionName,
        string removedColumn)
    {
        var rule = state.DisplayRule;
        if (rule is null)
        {
            return;
        }

        var missing = FindMissingReferences(rule, state, out _);
        if (missing.Count == 0)
        {
            return;
        }

        _logger.LogWarning(
            "DisplayRule conflict: rule references a removed column and needs repair. Collection: {CollectionName}, RemovedColumn: {ColumnName}, MissingColumns: {MissingColumns}, Template: {DisplayRuleTemplate}",
            collectionName,
            removedColumn,
            string.Join(",", missing),
            rule.Template);
    }

    public DisplayRule? GetDisplayRule(string collectionName)
    {
        collectionName = StorageValidation.NormalizeName(collectionName, "collection name");

        lock (_sync)
        {
            return GetCollectionState(collectionName).DisplayRule;
        }
    }

    /// <summary>
    /// Column references in the rule that the collection does not define.
    /// Deterministic; used for both validation and conflict detection.
    /// </summary>
    private static List<string> FindMissingReferences(
        DisplayRule rule,
        CollectionState state,
        out string? parseError)
    {
        var compiled = CompiledDisplayRule.Parse(rule.Template);
        parseError = compiled.ParseError;
        if (!compiled.IsValidSyntax)
        {
            return [];
        }

        return compiled.ColumnReferences
            .Where(reference => !state.Columns.ContainsKey(reference))
            .ToList();
    }

    public CollectionDefinition? GetCollectionDefinition(string collectionName)
    {
        collectionName = StorageValidation.NormalizeName(collectionName, "collection name");

        lock (_sync)
        {
            return _collections.TryGetValue(collectionName, out var state) ? state.Definition : null;
        }
    }

    public IReadOnlyCollection<CollectionDefinition> GetCollectionDefinitions()
    {
        lock (_sync)
        {
            return _collections.Values.Select(state => state.Definition).ToArray();
        }
    }

    public void AddColumn(string collectionName, ColumnDefinition column)
    {
        collectionName = StorageValidation.NormalizeName(collectionName, "collection name");
        ArgumentNullException.ThrowIfNull(column);

        lock (_sync)
        {
            ValidateSemanticType(column);
            var state = GetCollectionState(collectionName);
            if (!state.Columns.TryAdd(column.Name, column))
            {
                throw new InvalidOperationException($"Column '{column.Name}' already exists in '{collectionName}'.");
            }
            ApplyNewColumnToRecords(state, column);
            ValidateUniqueConstraints(state, null, null);
            ValidateAllRelations();
            state.IncrementSchemaVersion();

            _logger.LogInformation(
                "Schema changed: column added. Collection: {CollectionName}, Column: {ColumnName}, ColumnType: {ColumnType}",
                collectionName,
                column.Name,
                column.Type);
        }
    }

    public bool UpdateColumn(string collectionName, string currentColumnName, ColumnDefinition updatedColumn)
    {
        collectionName = StorageValidation.NormalizeName(collectionName, "collection name");
        currentColumnName = StorageValidation.NormalizeName(currentColumnName, "column name");
        ArgumentNullException.ThrowIfNull(updatedColumn);

        lock (_sync)
        {
            ValidateSemanticType(updatedColumn);
            var state = GetCollectionState(collectionName);
            if (!state.Columns.TryGetValue(currentColumnName, out var existingColumn))
            {
                return false;
            }

            if (!string.Equals(existingColumn.Name, updatedColumn.Name, StringComparison.OrdinalIgnoreCase) &&
                state.Columns.ContainsKey(updatedColumn.Name))
            {
                throw new InvalidOperationException($"Column '{updatedColumn.Name}' already exists in '{collectionName}'.");
            }

            ValidateExistingValuesForColumnType(state, existingColumn.Name, updatedColumn.Type);

            state.Columns.Remove(existingColumn.Name);
            state.Columns.Add(updatedColumn.Name, updatedColumn);
            RenameColumnOnRecords(state, existingColumn.Name, updatedColumn.Name);
            EnsureDefaultValuesForColumn(state, updatedColumn);
            UpdateRelationsAfterColumnRename(collectionName, existingColumn.Name, updatedColumn.Name);
            ValidateCollectionRecords(state);
            ValidateRelationDefinitions();
            ValidateAllRelations();
            state.IncrementSchemaVersion();

            if (!string.Equals(existingColumn.Name, updatedColumn.Name, StringComparison.OrdinalIgnoreCase))
            {
                RepairDisplayRuleAfterRename(state, collectionName, existingColumn.Name, updatedColumn.Name);
            }

            _logger.LogInformation(
                "Schema changed: column updated. Collection: {CollectionName}, Column: {ColumnName}, NewName: {NewColumnName}, ColumnType: {ColumnType}",
                collectionName,
                currentColumnName,
                updatedColumn.Name,
                updatedColumn.Type);

            return true;
        }
    }

    public bool RemoveColumn(string collectionName, string columnName)
    {
        collectionName = StorageValidation.NormalizeName(collectionName, "collection name");
        columnName = StorageValidation.NormalizeName(columnName, "column name");

        lock (_sync)
        {
            var state = GetCollectionState(collectionName);
            if (!state.Columns.ContainsKey(columnName))
            {
                return false;
            }

            if (_relations.Values.Any(relation =>
                    (string.Equals(relation.SourceCollection, collectionName, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(relation.SourceColumn, columnName, StringComparison.OrdinalIgnoreCase)) ||
                    (string.Equals(relation.TargetCollection, collectionName, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(relation.TargetColumn, columnName, StringComparison.OrdinalIgnoreCase))))
            {
                _logger.LogWarning(
                    "Column removal rejected, relations depend on it. Collection: {CollectionName}, Column: {ColumnName}",
                    collectionName,
                    columnName);
                throw new InvalidOperationException($"Column '{columnName}' is used by one or more relations.");
            }

            _logger.LogInformation(
                "Schema changed: column removed. Collection: {CollectionName}, Column: {ColumnName}",
                collectionName,
                columnName);

            state.Columns.Remove(columnName);
            ReportDisplayRuleConflictAfterRemoval(state, collectionName, columnName);
            foreach (var record in state.Records.Values.ToArray())
            {
                var updatedFields = new Dictionary<string, object?>(record.Fields, StringComparer.Ordinal);
                updatedFields.Remove(columnName);
                state.Records[record.Id] = new StorageRecord(record.Id, record.CollectionName, updatedFields);
            }

            state.IncrementSchemaVersion();
            return true;
        }
    }

    public void AddRelation(RelationDefinition relation)
    {
        ArgumentNullException.ThrowIfNull(relation);

        lock (_sync)
        {
            if (_relations.ContainsKey(relation.Name))
            {
                throw new InvalidOperationException($"Relation '{relation.Name}' already exists.");
            }

            ValidateRelationDefinition(relation);
            _relations.Add(relation.Name, relation);

            try
            {
                ValidateAllRelations();
                IncrementSchemaVersionsForRelation(relation);
            }
            catch (Exception ex)
            {
                // Roll back the relation so storage stays consistent, then let
                // the caller see the failure.
                _relations.Remove(relation.Name);
                _logger.LogError(
                    ex,
                    "Schema change failed: relation rolled back. Relation: {RelationName}, Source: {SourceCollection}, Target: {TargetCollection}",
                    relation.Name,
                    relation.SourceCollection,
                    relation.TargetCollection);
                throw;
            }

            _logger.LogInformation(
                "Schema changed: relation added. Relation: {RelationName}, Source: {SourceCollection}, Target: {TargetCollection}, RelationType: {RelationType}",
                relation.Name,
                relation.SourceCollection,
                relation.TargetCollection,
                relation.Type);
        }
    }

    public bool RemoveRelation(string relationName)
    {
        relationName = StorageValidation.NormalizeName(relationName, "relation name");

        lock (_sync)
        {
            if (!_relations.TryGetValue(relationName, out var relation))
            {
                return false;
            }

            var removed = _relations.Remove(relationName);
            if (removed)
            {
                IncrementSchemaVersionsForRelation(relation);
                _logger.LogInformation(
                    "Schema changed: relation removed. Relation: {RelationName}",
                    relationName);
            }

            return removed;
        }
    }

    public RelationDefinition? GetRelation(string relationName)
    {
        relationName = StorageValidation.NormalizeName(relationName, "relation name");

        lock (_sync)
        {
            return _relations.TryGetValue(relationName, out var relation) ? relation : null;
        }
    }

    public IReadOnlyCollection<RelationDefinition> GetRelations()
    {
        lock (_sync)
        {
            return _relations.Values.ToArray();
        }
    }

    public StorageRecord Create(string collectionName, IReadOnlyDictionary<string, object?> fields)
    {
        collectionName = StorageValidation.NormalizeName(collectionName, "collection name");
        ArgumentNullException.ThrowIfNull(fields);

        lock (_sync)
        {
            var state = GetCollectionState(collectionName);
            var normalizedFields = ValidateRecordFields(state, fields, null, null, _semanticTypeRegistry);

            var record = new StorageRecord(Ulid.NewUlid(), collectionName, normalizedFields);
            state.Records.Add(record.Id, record);

            try
            {
                ValidateAllRelations();
            }
            catch (Exception ex)
            {
                state.Records.Remove(record.Id);
                _logger.LogError(
                    ex,
                    "Record insertion failed relation validation and was rolled back. Collection: {CollectionName}, RecordId: {RecordId}",
                    collectionName,
                    record.Id);
                throw;
            }

            // Field values are deliberately not logged: records may hold large
            // or personal data.
            _logger.LogDebug(
                "Record inserted. Collection: {CollectionName}, RecordId: {RecordId}, FieldCount: {FieldCount}",
                collectionName,
                record.Id,
                normalizedFields.Count);

            return Clone(record);
        }
    }

    public StorageRecord? GetById(string collectionName, Ulid id)
    {
        collectionName = StorageValidation.NormalizeName(collectionName, "collection name");

        lock (_sync)
        {
            var state = GetCollectionState(collectionName);
            return state.Records.TryGetValue(id, out var record) ? Clone(record) : null;
        }
    }

    public bool Update(StorageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_sync)
        {
            var state = GetCollectionState(record.CollectionName);
            if (!state.Records.TryGetValue(record.Id, out var existingRecord))
            {
                return false;
            }

            var normalizedFields = ValidateRecordFields(state, record.Fields, record.Id, existingRecord, _semanticTypeRegistry);
            var replacement = new StorageRecord(record.Id, record.CollectionName, normalizedFields);
            state.Records[record.Id] = replacement;

            try
            {
                ValidateAllRelations();
            }
            catch (Exception ex)
            {
                state.Records[record.Id] = existingRecord;
                _logger.LogError(
                    ex,
                    "Record update failed relation validation and was rolled back. Collection: {CollectionName}, RecordId: {RecordId}",
                    record.CollectionName,
                    record.Id);
                throw;
            }

            _logger.LogDebug(
                "Record updated. Collection: {CollectionName}, RecordId: {RecordId}, FieldCount: {FieldCount}",
                record.CollectionName,
                record.Id,
                normalizedFields.Count);

            return true;
        }
    }

    public bool Delete(string collectionName, Ulid id)
    {
        collectionName = StorageValidation.NormalizeName(collectionName, "collection name");

        lock (_sync)
        {
            var state = GetCollectionState(collectionName);
            if (!state.Records.TryGetValue(id, out var existing))
            {
                return false;
            }

            state.Records.Remove(id);
            try
            {
                ValidateAllRelations();
            }
            catch (Exception ex)
            {
                state.Records[id] = existing;
                _logger.LogError(
                    ex,
                    "Record deletion failed relation validation and was rolled back. Collection: {CollectionName}, RecordId: {RecordId}",
                    collectionName,
                    id);
                throw;
            }

            _logger.LogDebug(
                "Record deleted. Collection: {CollectionName}, RecordId: {RecordId}",
                collectionName,
                id);

            return true;
        }
    }

    public IReadOnlyCollection<StorageRecord> GetAll(string collectionName)
    {
        collectionName = StorageValidation.NormalizeName(collectionName, "collection name");

        lock (_sync)
        {
            var state = GetCollectionState(collectionName);
            return state.Records.Values.Select(Clone).ToArray();
        }
    }

    public IReadOnlyCollection<StorageRecord> QueryByFieldValue(string collectionName, string fieldName, object? fieldValue)
    {
        collectionName = StorageValidation.NormalizeName(collectionName, "collection name");
        fieldName = StorageValidation.NormalizeName(fieldName, "field name");

        lock (_sync)
        {
            var state = GetCollectionState(collectionName);
            if (!state.Columns.ContainsKey(fieldName))
            {
                throw new InvalidOperationException($"Column '{fieldName}' does not exist in collection '{collectionName}'.");
            }

            return state.Records.Values
                .Where(record => record.Fields.TryGetValue(fieldName, out var value) && Equals(value, fieldValue))
                .Select(Clone)
                .ToArray();
        }
    }

    private void ValidateRelationDefinitions()
    {
        foreach (var relation in _relations.Values)
        {
            ValidateRelationDefinition(relation);
        }
    }

    private void ValidateRelationDefinition(RelationDefinition relation)
    {
        var sourceCollection = GetCollectionState(relation.SourceCollection);
        var targetCollection = GetCollectionState(relation.TargetCollection);

        if (!sourceCollection.Columns.TryGetValue(relation.SourceColumn, out var sourceColumn))
        {
            throw new InvalidOperationException(
                $"Relation '{relation.Name}' references missing source column '{relation.SourceColumn}'.");
        }

        if (!targetCollection.Columns.TryGetValue(relation.TargetColumn, out var targetColumn))
        {
            throw new InvalidOperationException(
                $"Relation '{relation.Name}' references missing target column '{relation.TargetColumn}'.");
        }

        if (sourceColumn.Type != targetColumn.Type)
        {
            throw new InvalidOperationException(
                $"Relation '{relation.Name}' columns '{relation.SourceColumn}' and '{relation.TargetColumn}' have incompatible types.");
        }

        var sourceUnique = sourceColumn.Unique;
        var targetUnique = targetColumn.Unique;

        if (relation.Type is RelationType.OneToOne && (!sourceUnique || !targetUnique))
        {
            throw new InvalidOperationException(
                $"Relation '{relation.Name}' of type OneToOne requires both columns to be unique or primary keys.");
        }

        if (relation.Type is RelationType.ManyToOne && !targetUnique)
        {
            throw new InvalidOperationException(
                $"Relation '{relation.Name}' of type ManyToOne requires target column '{relation.TargetColumn}' to be unique or primary key.");
        }

        if (relation.Type is RelationType.OneToMany && !sourceUnique)
        {
            throw new InvalidOperationException(
                $"Relation '{relation.Name}' of type OneToMany requires source column '{relation.SourceColumn}' to be unique or primary key.");
        }
    }

    private void ValidateAllRelations()
    {
        var errors = new List<StorageValidationError>();

        foreach (var relation in _relations.Values)
        {
            ValidateRelationData(relation, errors);
        }

        if (errors.Count > 0)
        {
            throw new StorageValidationException(errors);
        }
    }

    private void ValidateRelationData(RelationDefinition relation, List<StorageValidationError> errors)
    {
        var sourceState = GetCollectionState(relation.SourceCollection);
        var targetState = GetCollectionState(relation.TargetCollection);
        var targetRecords = targetState.Records.Values.ToArray();

        var sourceToTargetMatches = new List<(Ulid sourceRecordId, Ulid targetRecordId)>();
        foreach (var sourceRecord in sourceState.Records.Values)
        {
            if (!sourceRecord.Fields.TryGetValue(relation.SourceColumn, out var sourceValue) || sourceValue is null)
            {
                continue;
            }

            var matchedTargets = targetRecords
                .Where(record => record.Fields.TryGetValue(relation.TargetColumn, out var targetValue) && Equals(targetValue, sourceValue))
                .ToArray();

            if (matchedTargets.Length == 0)
            {
                errors.Add(new StorageValidationError(
                    "MissingReferencedRecord",
                    relation.SourceColumn,
                    $"Relation '{relation.Name}' requires a referenced record in '{relation.TargetCollection}' where '{relation.TargetColumn}' equals '{sourceValue}'."));
                continue;
            }

            if ((relation.Type is RelationType.OneToOne or RelationType.ManyToOne) && matchedTargets.Length > 1)
            {
                errors.Add(new StorageValidationError(
                    "AmbiguousReferencedRecord",
                    relation.TargetColumn,
                    $"Relation '{relation.Name}' found multiple target records for source value '{sourceValue}'."));
                continue;
            }

            foreach (var target in matchedTargets)
            {
                sourceToTargetMatches.Add((sourceRecord.Id, target.Id));
            }
        }

        if (relation.Type is RelationType.OneToOne or RelationType.OneToMany)
        {
            var duplicateTargetMatches = sourceToTargetMatches
                .GroupBy(match => match.targetRecordId)
                .Where(group => group.Select(x => x.sourceRecordId).Distinct().Count() > 1)
                .ToArray();

            foreach (var duplicate in duplicateTargetMatches)
            {
                errors.Add(new StorageValidationError(
                    "RelationCardinalityViolation",
                    relation.TargetColumn,
                    $"Relation '{relation.Name}' does not allow multiple source records to reference the same target record '{duplicate.Key}'."));
            }
        }
    }

    private void UpdateRelationsAfterColumnRename(string collectionName, string previousColumnName, string newColumnName)
    {
        if (string.Equals(previousColumnName, newColumnName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var relation in _relations.Values.ToArray())
        {
            var sourceColumn = relation.SourceColumn;
            var targetColumn = relation.TargetColumn;
            if (string.Equals(relation.SourceCollection, collectionName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(relation.SourceColumn, previousColumnName, StringComparison.OrdinalIgnoreCase))
            {
                sourceColumn = newColumnName;
            }

            if (string.Equals(relation.TargetCollection, collectionName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(relation.TargetColumn, previousColumnName, StringComparison.OrdinalIgnoreCase))
            {
                targetColumn = newColumnName;
            }

            _relations[relation.Name] = new RelationDefinition(
                relation.Name,
                relation.Type,
                relation.SourceCollection,
                sourceColumn,
                relation.TargetCollection,
                targetColumn,
                relation.Description);
        }
    }

    private static void ValidateExistingValuesForColumnType(CollectionState state, string columnName, Core.ColumnType columnType)
    {
        foreach (var record in state.Records.Values)
        {
            if (record.Fields.TryGetValue(columnName, out var value) &&
                !StorageValidation.IsValueCompatible(columnType, value))
            {
                throw new InvalidOperationException(
                    $"Existing value for column '{columnName}' is incompatible with type '{columnType}'.");
            }
        }
    }

    private static void RenameColumnOnRecords(CollectionState state, string currentColumnName, string updatedColumnName)
    {
        if (string.Equals(currentColumnName, updatedColumnName, StringComparison.Ordinal))
        {
            return;
        }

        foreach (var record in state.Records.Values.ToArray())
        {
            if (!record.Fields.TryGetValue(currentColumnName, out var value))
            {
                continue;
            }

            var updatedFields = new Dictionary<string, object?>(record.Fields, StringComparer.Ordinal);
            updatedFields.Remove(currentColumnName);
            updatedFields[updatedColumnName] = value;
            state.Records[record.Id] = new StorageRecord(record.Id, record.CollectionName, updatedFields);
        }
    }

    private static void ApplyNewColumnToRecords(CollectionState state, ColumnDefinition column)
    {
        foreach (var record in state.Records.Values.ToArray())
        {
            var updatedFields = new Dictionary<string, object?>(record.Fields, StringComparer.Ordinal)
            {
                [column.Name] = column.DefaultValue
            };
            state.Records[record.Id] = new StorageRecord(record.Id, record.CollectionName, updatedFields);
        }
    }

    private static void EnsureDefaultValuesForColumn(CollectionState state, ColumnDefinition column)
    {
        foreach (var record in state.Records.Values.ToArray())
        {
            if (record.Fields.ContainsKey(column.Name))
            {
                continue;
            }

            var updatedFields = new Dictionary<string, object?>(record.Fields, StringComparer.Ordinal)
            {
                [column.Name] = column.DefaultValue
            };
            state.Records[record.Id] = new StorageRecord(record.Id, record.CollectionName, updatedFields);
        }
    }

    private void ValidateCollectionRecords(CollectionState state)
    {
        foreach (var record in state.Records.Values.ToArray())
        {
            var normalizedFields = ValidateRecordFields(state, record.Fields, record.Id, record, _semanticTypeRegistry);
            state.Records[record.Id] = new StorageRecord(record.Id, record.CollectionName, normalizedFields);
        }
    }

    private static Dictionary<string, object?> ValidateRecordFields(
        CollectionState state,
        IReadOnlyDictionary<string, object?> fields,
        Ulid? currentRecordId,
        StorageRecord? existingRecord,
        ISemanticTypeRegistry? semanticTypeRegistry)
    {
        var errors = new List<StorageValidationError>();

        // Only names are checked here. The value of every column - provided or
        // defaulted - is checked by the loop below, against the value that will
        // actually be stored; doing it in both places reported one fault twice,
        // in two slightly different sentences.
        foreach (var field in fields.Where(field => !state.Columns.ContainsKey(field.Key)))
        {
            errors.Add(new StorageValidationError(
                "UnknownColumn",
                field.Key,
                $"Column '{field.Key}' does not exist in collection '{state.Definition.Name}'."));
        }

        var normalizedFields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var column in state.Columns.Values)
        {
            var hasProvidedValue = fields.TryGetValue(column.Name, out var value);
            if (!hasProvidedValue)
            {
                value = column.DefaultValue;
            }

            var semanticType = ResolveSemanticType(column, semanticTypeRegistry);
            var normalizationFailed = false;
            if (!string.IsNullOrWhiteSpace(column.SemanticTypeName) && semanticType is null)
            {
                errors.Add(new StorageValidationError(
                    "InvalidSemanticType",
                    column.Name,
                    $"Semantic type '{column.SemanticTypeName}' is not available."));
                normalizationFailed = true;
            }

            if (value is not null && semanticType is not null)
            {
                try
                {
                    value = StorageValidation.ApplyNormalizationRules(value, semanticType.NormalizationRules);
                }
                catch (Exception ex)
                {
                    errors.Add(new StorageValidationError(
                        "NormalizationFailed",
                        column.Name,
                        $"Column '{column.Name}' normalization failed: {ex.Message}"));
                    normalizationFailed = true;
                }
            }

            var typeCompatible = value is null || StorageValidation.IsValueCompatible(column.Type, value);
            if (!typeCompatible)
            {
                errors.Add(new StorageValidationError(
                    "InvalidType",
                    column.Name,
                    $"Column '{column.Name}' value is incompatible with type '{column.Type}'."));
            }

            // Only a value of the declared type is worth measuring against the
            // semantic rules; one that is not has already been reported above,
            // and feeding it to the rules would report the same fault twice in
            // less useful words.
            if (!normalizationFailed && typeCompatible && value is not null && semanticType is not null)
            {
                var failing = SemanticValidationRules.Failing(semanticType.Validations, value);
                if (failing.Count > 0)
                {
                    errors.Add(new StorageValidationError(
                        "InvalidSemanticValue",
                        column.Name,
                        DescribeSemanticFailure(column, semanticType, value, failing)));
                }
            }

            if (!normalizationFailed &&
                value is not null &&
                !StorageValidation.MatchesAllValidationPatterns(value, column.ValidationPatterns))
            {
                errors.Add(new StorageValidationError(
                    "InvalidColumnValidation",
                    column.Name,
                    $"Column '{column.Name}' value does not satisfy column validation rules."));
            }

            normalizedFields[column.Name] = value;
        }

        ValidateReadOnlyColumns(state, normalizedFields, existingRecord, errors);
        ValidateUniqueConstraints(state, normalizedFields, currentRecordId, errors);

        if (errors.Count > 0)
        {
            throw new StorageValidationException(errors);
        }

        return normalizedFields;
    }

    private static void ValidateReadOnlyColumns(
        CollectionState state,
        Dictionary<string, object?> normalizedFields,
        StorageRecord? existingRecord,
        List<StorageValidationError> errors)
    {
        if (existingRecord is null)
        {
            return;
        }

        foreach (var column in state.Columns.Values.Where(column => column.ReadOnly))
        {
            existingRecord.Fields.TryGetValue(column.Name, out var currentValue);
            normalizedFields.TryGetValue(column.Name, out var newValue);

            if (!Equals(currentValue, newValue))
            {
                errors.Add(new StorageValidationError(
                    "ReadOnlyColumn",
                    column.Name,
                    $"Column '{column.Name}' is read-only and cannot be changed."));
            }
        }
    }

    private static void ValidateUniqueConstraints(
        CollectionState state,
        Dictionary<string, object?>? normalizedFields,
        Ulid? currentRecordId,
        List<StorageValidationError>? errors = null)
    {
        var uniqueColumns = state.Columns.Values.Where(column => column.Unique).ToArray();
        if (uniqueColumns.Length == 0)
        {
            return;
        }

        if (normalizedFields is null)
        {
            foreach (var column in uniqueColumns)
            {
                var values = state.Records.Values
                    .Select(record => record.Fields.TryGetValue(column.Name, out var value) ? value : null)
                    .Where(value => value is not null)
                    .ToArray();

                if (values.Length != values.Distinct().Count())
                {
                    throw new InvalidOperationException($"Existing data violates unique constraint on '{column.Name}'.");
                }
            }

            return;
        }

        foreach (var column in uniqueColumns)
        {
            var candidateValue = normalizedFields[column.Name];
            if (candidateValue is null)
            {
                continue;
            }

            var hasDuplicate = state.Records.Values.Any(record =>
            {
                if (currentRecordId.HasValue && record.Id == currentRecordId.Value)
                {
                    return false;
                }

                return record.Fields.TryGetValue(column.Name, out var existingValue) &&
                       Equals(existingValue, candidateValue);
            });

            if (hasDuplicate)
            {
                errors?.Add(new StorageValidationError(
                    "UniqueConstraint",
                    column.Name,
                    $"Unique constraint failed for column '{column.Name}'."));
            }
        }
    }

    private CollectionState GetCollectionState(string collectionName)
    {
        if (_collections.TryGetValue(collectionName, out var state))
        {
            return state;
        }

        throw new InvalidOperationException($"Collection '{collectionName}' does not exist.");
    }

    private static StorageRecord Clone(StorageRecord record)
    {
        return new StorageRecord(record.Id, record.CollectionName, record.Fields);
    }

    private void ValidateSemanticTypes(IEnumerable<ColumnDefinition> columns)
    {
        foreach (var column in columns)
        {
            ValidateSemanticType(column);
        }
    }

    private void ValidateSemanticType(ColumnDefinition column)
    {
        if (string.IsNullOrWhiteSpace(column.SemanticTypeName))
        {
            return;
        }

        if (_semanticTypeRegistry is null)
        {
            throw new InvalidOperationException(
                $"Semantic type '{column.SemanticTypeName}' cannot be used because no semantic type registry is configured.");
        }

        var semanticType = _semanticTypeRegistry.GetByNameOrAlias(column.SemanticTypeName);
        if (semanticType is null)
        {
            throw new InvalidOperationException(
                $"Semantic type '{column.SemanticTypeName}' is not registered.");
        }

        if (semanticType.BaseType != column.Type)
        {
            throw new InvalidOperationException(
                $"Semantic type '{semanticType.Name}' base type '{semanticType.BaseType}' is incompatible with column type '{column.Type}'.");
        }
    }

    private static SemanticTypeDefinition? ResolveSemanticType(
        ColumnDefinition column,
        ISemanticTypeRegistry? semanticTypeRegistry)
    {
        if (string.IsNullOrWhiteSpace(column.SemanticTypeName))
        {
            return null;
        }

        if (semanticTypeRegistry is null)
        {
            return null;
        }

        return semanticTypeRegistry.GetByNameOrAlias(column.SemanticTypeName);
    }

    /// <summary>
    /// Explains a rejection to whoever supplied the value: which column, what
    /// was offered, and what each failed rule actually requires - plus the
    /// type's own examples, which answer "so what would be valid?" better than
    /// any rule can.
    ///
    /// The value is echoed back because the caller already has it, but it is
    /// truncated: a record field can be long, and this text travels.
    /// </summary>
    private static string DescribeSemanticFailure(
        ColumnDefinition column,
        SemanticTypeDefinition semanticType,
        object value,
        IReadOnlyList<SemanticValidation> failing)
    {
        var requirements = string.Join("; ", failing.Select(SemanticValidationRules.Describe));

        var message =
            $"Column '{column.Name}' value '{Preview(value)}' does not satisfy semantic type " +
            $"'{semanticType.Name}': {requirements}.";

        var examples = (semanticType.Examples ?? Array.Empty<string>())
            .Where(example => !string.IsNullOrWhiteSpace(example))
            .Take(3)
            .ToArray();

        return examples.Length == 0
            ? message
            : $"{message} Valid examples: {string.Join(", ", examples)}.";
    }

    private const int ValuePreviewLength = 60;

    private static string Preview(object value)
    {
        var text = RecordValueFormatter.Format(value);
        return text.Length <= ValuePreviewLength
            ? text
            : string.Concat(text.AsSpan(0, ValuePreviewLength), "...");
    }

    private static CollectionDefinition EnsureSchemaVersion(CollectionDefinition definition, int fallbackVersion)
    {
        var metadata = new Dictionary<string, string?>(definition.Metadata, StringComparer.Ordinal);
        if (!metadata.TryGetValue(SchemaVersionMetadataKey, out var rawVersion) ||
            !int.TryParse(rawVersion, out var parsedVersion) ||
            parsedVersion < 1)
        {
            metadata[SchemaVersionMetadataKey] = fallbackVersion.ToString(CultureInfo.InvariantCulture);
        }

        return new CollectionDefinition(
            definition.Name,
            definition.Description,
            definition.Columns,
            metadata,
            definition.DisplayRule);
    }

    private void IncrementSchemaVersionsForRelation(RelationDefinition relation)
    {
        var sourceState = GetCollectionState(relation.SourceCollection);
        sourceState.IncrementSchemaVersion();

        if (!string.Equals(relation.SourceCollection, relation.TargetCollection, StringComparison.OrdinalIgnoreCase))
        {
            var targetState = GetCollectionState(relation.TargetCollection);
            targetState.IncrementSchemaVersion();
        }
    }

    private sealed class CollectionState
    {
        public CollectionState(CollectionDefinition definition)
        {
            Definition = EnsureSchemaVersion(definition, 1);
            DisplayRule = definition.DisplayRule;
            Columns = Definition.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
            Metadata = new Dictionary<string, string?>(Definition.Metadata, StringComparer.Ordinal);
        }

        public CollectionDefinition Definition { get; private set; }

        /// <summary>Current display rule; survives schema-version rebuilds.</summary>
        public DisplayRule? DisplayRule { get; set; }

        public Dictionary<string, ColumnDefinition> Columns { get; }

        public Dictionary<Ulid, StorageRecord> Records { get; } = new();

        public Dictionary<string, string?> Metadata { get; }

        public int SchemaVersion =>
            Metadata.TryGetValue(SchemaVersionMetadataKey, out var raw) && int.TryParse(raw, out var parsed) && parsed > 0
                ? parsed
                : 1;

        public void RebuildDefinition()
        {
            Definition = new CollectionDefinition(
                Definition.Name,
                Definition.Description,
                Columns.Values.ToArray(),
                Metadata,
                DisplayRule);
        }

        public void IncrementSchemaVersion()
        {
            Metadata[SchemaVersionMetadataKey] = (SchemaVersion + 1).ToString();
            RebuildDefinition();
        }
    }
}
