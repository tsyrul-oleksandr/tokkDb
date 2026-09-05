using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.RegularExpressions;
using TokkDb.LLM.Core;
using TokkDb.LLM.Storage;
using StorageColumnDefinition = TokkDb.LLM.Storage.ColumnDefinition;

namespace TokkDb.LLM.Application;

public sealed partial class StorageToolGateway : IStorageToolGateway
{
    private readonly IStorageRuntime _storageRuntime;
    private readonly ISemanticTypeRegistry _semanticTypeRegistry;
    private readonly ISchemaChangeProposalStore _schemaProposalStore;
    private readonly SchemaToolOptions _schemaToolOptions;
    private readonly IDisplayRuleEvaluator _displayRuleEvaluator;
    private readonly IDisplayRuleValidator _displayRuleValidator;
    private readonly IRecordDisplayService _recordDisplayService;
    private readonly IRecordQueryBinder _recordQueryBinder;
    private readonly ILogger<StorageToolGateway> _logger;

    public StorageToolGateway(
        IStorageRuntime storageRuntime,
        ISemanticTypeRegistry semanticTypeRegistry,
        ISchemaChangeProposalStore schemaProposalStore,
        SchemaToolOptions schemaToolOptions,
        IDisplayRuleEvaluator displayRuleEvaluator,
        IDisplayRuleValidator displayRuleValidator,
        IRecordDisplayService recordDisplayService,
        IRecordQueryBinder recordQueryBinder,
        ILogger<StorageToolGateway> logger)
    {
        _storageRuntime = storageRuntime;
        _semanticTypeRegistry = semanticTypeRegistry;
        _schemaProposalStore = schemaProposalStore;
        _schemaToolOptions = schemaToolOptions;
        _displayRuleEvaluator = displayRuleEvaluator;
        _displayRuleValidator = displayRuleValidator;
        _recordDisplayService = recordDisplayService;
        _recordQueryBinder = recordQueryBinder;
        _logger = logger;
    }

    public StorageToolResult<IReadOnlyCollection<SemanticTypeToolResult>> GetSemanticTypes()
    {
        var semanticTypes = _semanticTypeRegistry.GetAll().Select(MapSemanticType).ToArray();
        return StorageToolResult<IReadOnlyCollection<SemanticTypeToolResult>>.Ok(semanticTypes);
    }

    public StorageToolResult<SemanticTypeToolResult> RegisterSemanticType(SemanticTypeToolDefinition semanticType)
    {
        if (semanticType is null)
        {
            return StorageToolResult<SemanticTypeToolResult>.Fail(new StorageToolError("InvalidSemanticType", "semanticType", "Semantic type definition is required."));
        }

        try
        {
            _logger.LogInformation("Semantic type registration started: {SemanticTypeName}", semanticType.Name);
            var definition = new SemanticTypeDefinition(
                semanticType.Name,
                semanticType.DisplayName,
                semanticType.Description,
                semanticType.BaseType,
                semanticType.ParentType,
                semanticType.Aliases ?? [],
                semanticType.Examples ?? [],
                semanticType.ValidationPattern,
                semanticType.ValidationPatterns,
                semanticType.NormalizationRules);
            _semanticTypeRegistry.Register(definition);

            var stored = _semanticTypeRegistry.GetByNameOrAlias(semanticType.Name);
            if (stored is null)
            {
                return StorageToolResult<SemanticTypeToolResult>.Fail(new StorageToolError("SemanticTypeError", "name", "Semantic type was registered but could not be loaded."));
            }

            _logger.LogInformation("Semantic type registration completed: {SemanticTypeName}", semanticType.Name);
            return StorageToolResult<SemanticTypeToolResult>.Ok(MapSemanticType(stored));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Semantic type registration failed: {SemanticTypeName}", semanticType.Name);
            return StorageToolResult<SemanticTypeToolResult>.Fail(new StorageToolError("SemanticTypeError", null, ex.Message));
        }
    }

    public StorageToolResult<IReadOnlyCollection<string>> GetCollections()
    {
        var names = _storageRuntime.Storage.GetCollectionDefinitions().Select(definition => definition.Name).OrderBy(x => x).ToArray();
        return StorageToolResult<IReadOnlyCollection<string>>.Ok(names);
    }

    public StorageToolResult<CollectionSchemaResult> GetCollectionSchema(string collectionName)
    {
        var invalidCollectionNameError = ValidateName(collectionName, "collectionName", "collection");
        if (invalidCollectionNameError is not null)
        {
            return StorageToolResult<CollectionSchemaResult>.Fail(invalidCollectionNameError);
        }

        var collection = _storageRuntime.Storage.GetCollectionDefinition(collectionName);
        if (collection is null)
        {
            return StorageToolResult<CollectionSchemaResult>.Fail(new StorageToolError("CollectionNotFound", "collectionName", $"Collection '{collectionName}' was not found."));
        }

        return StorageToolResult<CollectionSchemaResult>.Ok(MapCollectionSchema(collection));
    }

    /// <summary>
    /// Aggregate analysis, validated and executed in one call.
    ///
    /// There is no stored plan. A plan the caller had to create, remember and
    /// then execute was three tool calls to answer one question, and the id in
    /// between was one more thing for a model to lose.
    /// </summary>
    public StorageToolResult<DataQueryExecutionResult> AnalyzeRecords(DataQueryDefinition definition)
    {
        if (definition is null)
        {
            return StorageToolResult<DataQueryExecutionResult>.Fail(
                new StorageToolError("InvalidDataQuery", "definition", "Data query definition is required."));
        }

        var collectionNameError = ValidateName(definition.CollectionName, "collectionName", "collection");
        if (collectionNameError is not null)
        {
            return StorageToolResult<DataQueryExecutionResult>.Fail(collectionNameError);
        }

        var collection = _storageRuntime.Storage.GetCollectionDefinition(definition.CollectionName);
        if (collection is null)
        {
            return StorageToolResult<DataQueryExecutionResult>.Fail(
                new StorageToolError("CollectionNotFound", "collectionName", $"Collection '{definition.CollectionName}' was not found."));
        }

        if (definition.Limit < 1 || definition.Limit > 500)
        {
            return StorageToolResult<DataQueryExecutionResult>.Fail(
                new StorageToolError("InvalidLimit", "limit", "Limit must be in range [1, 500]."));
        }

        var normalizedDefinition = NormalizeDataQueryDefinition(definition, collection);
        var validationErrors = ValidateDataQueryDefinition(normalizedDefinition, collection).ToArray();
        if (validationErrors.Length > 0)
        {
            return StorageToolResult<DataQueryExecutionResult>.Fail(validationErrors);
        }

        try
        {
            var rows = ExecuteDataQuery(normalizedDefinition, collection);
            var execution = new DataQueryExecutionResult(
                BuildDataQuerySummary(normalizedDefinition),
                rows.Count,
                rows,
                BuildSemanticHints(collection, normalizedDefinition).ToArray());

            _logger.LogInformation(
                "Data query executed. QueryType: {QueryType}, Collection: {CollectionName}, Returned: {RowCount}",
                normalizedDefinition.QueryType,
                normalizedDefinition.CollectionName,
                execution.TotalRows);

            return StorageToolResult<DataQueryExecutionResult>.Ok(execution);
        }
        catch (Exception ex)
        {
            // Internal detail stays in the log, never in the chat.
            _logger.LogError(
                ex,
                "Data query failed. QueryType: {QueryType}, Collection: {CollectionName}",
                normalizedDefinition.QueryType,
                normalizedDefinition.CollectionName);

            return StorageToolResult<DataQueryExecutionResult>.Fail(
                new StorageToolError("DataQueryFailed", null, "The analysis could not be completed."));
        }
    }

    private StorageToolResult<SchemaChangeProposal> CreateSchemaChangeProposal(SchemaChangeProposalRequest request)
    {
        if (request is null)
        {
            return StorageToolResult<SchemaChangeProposal>.Fail(new StorageToolError("InvalidProposal", "request", "Schema change proposal request is required."));
        }

        var invalidCollectionNameError = ValidateName(request.TargetCollection, "targetCollection", "collection");
        if (invalidCollectionNameError is not null)
        {
            return StorageToolResult<SchemaChangeProposal>.Fail(invalidCollectionNameError);
        }

        var requiresColumn = RequiresTargetColumn(request.OperationType);
        if (requiresColumn)
        {
            var invalidColumnNameError = ValidateName(request.TargetColumn ?? string.Empty, "targetColumn", "column");
            if (invalidColumnNameError is not null)
            {
                return StorageToolResult<SchemaChangeProposal>.Fail(invalidColumnNameError);
            }
        }

        /*if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return StorageToolResult<SchemaChangeProposal>.Fail(
                new StorageToolError("InvalidReason", "reason", "Proposal reason is required."));
        }

        if (string.IsNullOrWhiteSpace(request.Source))
        {
            return StorageToolResult<SchemaChangeProposal>.Fail(
                new StorageToolError("InvalidSource", "source", "Proposal source is required."));
        }*/

        var definitionValidationError = ValidateProposalAgainstCurrentSchema(request.OperationType, request.TargetCollection, request.TargetColumn);
        if (definitionValidationError is not null)
        {
            return StorageToolResult<SchemaChangeProposal>.Fail(definitionValidationError);
        }

        var affectedData = BuildAffectedDataImpact(request.TargetCollection, request.TargetColumn);
        var affectedQueries = BuildSavedQueriesImpact(request.TargetCollection, request.TargetColumn);
        var affectedRelations = GetAffectedRelations(request.TargetCollection, request.TargetColumn);
        var affectedRules = GetAffectedRules(request.TargetCollection, request.TargetColumn);
        var requiredUserAction = string.IsNullOrWhiteSpace(request.RequiredUserAction)
            ? "ReviewAndApproveSchemaChange"
            : request.RequiredUserAction.Trim();

        var proposalWithoutAnalysis = new SchemaChangeProposal(
            Guid.NewGuid().ToString("N"),
            request.OperationType,
            request.TargetCollection.Trim(),
            string.IsNullOrWhiteSpace(request.TargetColumn) ? null : request.TargetColumn.Trim(),
            string.IsNullOrWhiteSpace(request.CurrentDefinition) ? null : request.CurrentDefinition.Trim(),
            request.ProposedDefinition?.Trim(),
            request.Definition,
            request.Reason.Trim(),
            request.Source.Trim(),
            affectedData,
            affectedQueries,
            affectedRelations,
            affectedRules,
            requiredUserAction,
            null,
            DateTimeOffset.UtcNow);
        var analysis = BuildImpactAnalysis(proposalWithoutAnalysis);

        var proposal = proposalWithoutAnalysis with
        {
            ImpactAnalysis = analysis,
            AffectedData = analysis.ExistingRecords,
            AffectedQueries = analysis.SavedQueries,
            AffectedRelations = analysis.Relations.Items,
            AffectedRules = analysis.ValidationRules.Items
        };

        _schemaProposalStore.Save(proposal);
        _logger.LogInformation(
            "Schema proposal created: {ProposalId} {OperationType} {Collection}.{Column} ({Classification})",
            proposal.ProposalId,
            proposal.OperationType,
            proposal.TargetCollection,
            proposal.TargetColumn ?? "<none>",
            analysis.Classification);
        return StorageToolResult<SchemaChangeProposal>.Ok(proposal);
    }

    private SchemaImpactAnalysis BuildImpactAnalysis(SchemaChangeProposal proposal)
    {
        var notes = new List<string>();
        var invalidReasons = new List<string>();
        var instructionReasons = new List<string>();

        var existingRecords = BuildAffectedDataImpact(proposal.TargetCollection, proposal.TargetColumn);
        var columns = BuildColumnsImpact(proposal.TargetCollection, proposal.TargetColumn);
        var relations = BuildRelationsImpact(proposal.TargetCollection, proposal.TargetColumn);
        var semanticTypes = BuildSemanticTypesImpact(proposal.TargetCollection, proposal.TargetColumn);
        var validationRules = BuildValidationRulesImpact(proposal.TargetCollection, proposal.TargetColumn);
        var displayRules = BuildDisplayRulesImpact(proposal.TargetCollection, proposal.TargetColumn);
        var savedQueries = BuildSavedQueriesImpact(proposal.TargetCollection, proposal.TargetColumn);
        var pendingOperations = BuildPendingOperationsImpact(proposal.TargetCollection, proposal.TargetColumn);
        var dependentTools = BuildDependentToolsImpact(proposal.TargetCollection, proposal.TargetColumn);

        ApplyOperationSpecificChecks(proposal, invalidReasons, instructionReasons);

        if (displayRules.EstimatedCount == 0)
        {
            notes.Add("No display rules are currently registered.");
        }

        if (savedQueries.EstimatedCount == 0)
        {
            notes.Add("No saved queries are currently registered.");
        }

        if (pendingOperations.EstimatedCount == 0)
        {
            notes.Add("No pending operations are currently registered.");
        }

        var classification = ClassifyImpact(
            invalidReasons,
            instructionReasons,
            existingRecords,
            columns,
            relations,
            semanticTypes,
            validationRules,
            displayRules,
            savedQueries,
            pendingOperations,
            dependentTools);

        var summary = BuildImpactSummary(classification, invalidReasons, instructionReasons);
        notes.AddRange(invalidReasons.Select(reason => $"Invalid: {reason}"));
        notes.AddRange(instructionReasons.Select(reason => $"Requires instruction: {reason}"));

        return new SchemaImpactAnalysis(
            classification,
            summary,
            existingRecords,
            columns,
            relations,
            semanticTypes,
            validationRules,
            displayRules,
            savedQueries,
            pendingOperations,
            dependentTools,
            notes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            DateTimeOffset.UtcNow);
    }

    public StorageToolResult<CollectionSchemaResult> CreateCollection(string collectionName, string? description, List<ColumnToolDefinition> columns)
    {
        var schemaChangeGuard = EnsureSchemaChangesAllowed(nameof(CreateCollection));
        if (schemaChangeGuard is not null)
        {
            return StorageToolResult<CollectionSchemaResult>.Fail(schemaChangeGuard);
        }

        var invalidCollectionNameError = ValidateName(collectionName, "collectionName", "collection");
        if (invalidCollectionNameError is not null)
        {
            return StorageToolResult<CollectionSchemaResult>.Fail(invalidCollectionNameError);
        }

        var impactGuard = EnsureImpactAnalysisCompleted(
            [SchemaChangeOperationType.CreateCollection],
            collectionName,
            null);
        if (impactGuard is not null)
        {
            return StorageToolResult<CollectionSchemaResult>.Fail(impactGuard);
        }

        columns ??= [];
        var columnDefinitionsResult = BuildColumnDefinitions(columns);
        if (!columnDefinitionsResult.Success || columnDefinitionsResult.Data is null)
        {
            return StorageToolResult<CollectionSchemaResult>.Fail(columnDefinitionsResult.Errors);
        }

        try
        {
            _logger.LogInformation("Schema change started: CreateCollection {CollectionName}", collectionName);
            _storageRuntime.Storage.CreateCollection(new CollectionDefinition(collectionName, description, columnDefinitionsResult.Data));
            var collection = _storageRuntime.Storage.GetCollectionDefinition(collectionName);
            if (collection is null)
            {
                return StorageToolResult<CollectionSchemaResult>.Fail(new StorageToolError("StorageError", "collectionName", "Collection was created but could not be loaded."));
            }

            _logger.LogInformation("Schema change completed: CreateCollection {CollectionName} v{SchemaVersion}", collectionName, GetSchemaVersion(collection));
            return StorageToolResult<CollectionSchemaResult>.Ok(MapCollectionSchema(collection));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schema change failed: CreateCollection {CollectionName}", collectionName);
            return StorageToolResult<CollectionSchemaResult>.Fail(new StorageToolError("SchemaChangeError", null, ex.Message));
        }
    }

    public StorageToolResult<SchemaChangeResult> AddColumn(string collectionName, ColumnToolDefinition column)
    {
        var schemaChangeGuard = EnsureSchemaChangesAllowed(nameof(AddColumn));
        if (schemaChangeGuard is not null)
        {
            return StorageToolResult<SchemaChangeResult>.Fail(schemaChangeGuard);
        }

        var invalidCollectionNameError = ValidateName(collectionName, "collectionName", "collection");
        if (invalidCollectionNameError is not null)
        {
            return StorageToolResult<SchemaChangeResult>.Fail(invalidCollectionNameError);
        }

        var impactGuard = EnsureImpactAnalysisCompleted(
            [SchemaChangeOperationType.AddColumn],
            collectionName,
            column.Name);
        if (impactGuard is not null)
        {
            return StorageToolResult<SchemaChangeResult>.Fail(impactGuard);
        }

        var columnResult = BuildColumnDefinition(column);
        if (!columnResult.Success || columnResult.Data is null)
        {
            return StorageToolResult<SchemaChangeResult>.Fail(columnResult.Errors);
        }

        var existingCollection = _storageRuntime.Storage.GetCollectionDefinition(collectionName);
        if (existingCollection is null)
        {
            return StorageToolResult<SchemaChangeResult>.Fail(new StorageToolError("CollectionNotFound", "collectionName", $"Collection '{collectionName}' was not found."));
        }

        try
        {
            _logger.LogInformation("Schema change started: AddColumn {CollectionName}.{ColumnName}", existingCollection.Name, columnResult.Data.Name);
            _storageRuntime.Storage.AddColumn(existingCollection.Name, columnResult.Data);
            var updatedCollection = _storageRuntime.Storage.GetCollectionDefinition(existingCollection.Name)!;
            _logger.LogInformation("Schema change completed: AddColumn {CollectionName}.{ColumnName} v{SchemaVersion}", existingCollection.Name, columnResult.Data.Name, GetSchemaVersion(updatedCollection));
            return StorageToolResult<SchemaChangeResult>.Ok(new SchemaChangeResult(existingCollection.Name, GetSchemaVersion(updatedCollection), "AddColumn"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schema change failed: AddColumn {CollectionName}", existingCollection.Name);
            return StorageToolResult<SchemaChangeResult>.Fail(new StorageToolError("SchemaChangeError", null, ex.Message));
        }
    }

    public StorageToolResult<SchemaChangeResult> ModifyColumn(string collectionName, string currentColumnName, ColumnToolDefinition updatedColumn)
    {
        var schemaChangeGuard = EnsureSchemaChangesAllowed(nameof(ModifyColumn));
        if (schemaChangeGuard is not null)
        {
            return StorageToolResult<SchemaChangeResult>.Fail(schemaChangeGuard);
        }

        var invalidCollectionNameError = ValidateName(collectionName, "collectionName", "collection");
        if (invalidCollectionNameError is not null)
        {
            return StorageToolResult<SchemaChangeResult>.Fail(invalidCollectionNameError);
        }

        var invalidCurrentColumnError = ValidateName(currentColumnName, "currentColumnName", "column");
        if (invalidCurrentColumnError is not null)
        {
            return StorageToolResult<SchemaChangeResult>.Fail(invalidCurrentColumnError);
        }

        var impactGuard = EnsureImpactAnalysisCompleted(
            [
                SchemaChangeOperationType.RenameColumn,
                SchemaChangeOperationType.ChangeColumnType,
                SchemaChangeOperationType.ChangeColumnDescription,
                SchemaChangeOperationType.ChangeSemanticType
            ],
            collectionName,
            currentColumnName);
        if (impactGuard is not null)
        {
            return StorageToolResult<SchemaChangeResult>.Fail(impactGuard);
        }

        var columnResult = BuildColumnDefinition(updatedColumn);
        if (!columnResult.Success || columnResult.Data is null)
        {
            return StorageToolResult<SchemaChangeResult>.Fail(columnResult.Errors);
        }

        var existingCollection = _storageRuntime.Storage.GetCollectionDefinition(collectionName);
        if (existingCollection is null)
        {
            return StorageToolResult<SchemaChangeResult>.Fail(new StorageToolError("CollectionNotFound", "collectionName", $"Collection '{collectionName}' was not found."));
        }

        try
        {
            _logger.LogInformation("Schema change started: ModifyColumn {CollectionName}.{ColumnName}", existingCollection.Name, currentColumnName);
            var updated = _storageRuntime.Storage.UpdateColumn(existingCollection.Name, currentColumnName, columnResult.Data);
            if (!updated)
            {
                return StorageToolResult<SchemaChangeResult>.Fail(new StorageToolError("ColumnNotFound", "currentColumnName", $"Column '{currentColumnName}' was not found in '{existingCollection.Name}'."));
            }

            var reloadedCollection = _storageRuntime.Storage.GetCollectionDefinition(existingCollection.Name)!;
            _logger.LogInformation("Schema change completed: ModifyColumn {CollectionName}.{ColumnName} v{SchemaVersion}", existingCollection.Name, currentColumnName, GetSchemaVersion(reloadedCollection));
            return StorageToolResult<SchemaChangeResult>.Ok(new SchemaChangeResult(existingCollection.Name, GetSchemaVersion(reloadedCollection), "ModifyColumn"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schema change failed: ModifyColumn {CollectionName}.{ColumnName}", existingCollection.Name, currentColumnName);
            return StorageToolResult<SchemaChangeResult>.Fail(new StorageToolError("SchemaChangeError", null, ex.Message));
        }
    }

    public StorageToolResult<SchemaChangeResult> DeleteColumn(string collectionName, string columnName)
    {
        var schemaChangeGuard = EnsureSchemaChangesAllowed(nameof(DeleteColumn));
        if (schemaChangeGuard is not null)
        {
            return StorageToolResult<SchemaChangeResult>.Fail(schemaChangeGuard);
        }

        var invalidCollectionNameError = ValidateName(collectionName, "collectionName", "collection");
        if (invalidCollectionNameError is not null)
        {
            return StorageToolResult<SchemaChangeResult>.Fail(invalidCollectionNameError);
        }

        var invalidColumnNameError = ValidateName(columnName, "columnName", "column");
        if (invalidColumnNameError is not null)
        {
            return StorageToolResult<SchemaChangeResult>.Fail(invalidColumnNameError);
        }

        var impactGuard = EnsureImpactAnalysisCompleted(
            [SchemaChangeOperationType.RemoveColumn],
            collectionName,
            columnName);
        if (impactGuard is not null)
        {
            return StorageToolResult<SchemaChangeResult>.Fail(impactGuard);
        }

        var existingCollection = _storageRuntime.Storage.GetCollectionDefinition(collectionName);
        if (existingCollection is null)
        {
            return StorageToolResult<SchemaChangeResult>.Fail(new StorageToolError("CollectionNotFound", "collectionName", $"Collection '{collectionName}' was not found."));
        }

        try
        {
            _logger.LogInformation("Schema change started: DeleteColumn {CollectionName}.{ColumnName}", existingCollection.Name, columnName);
            var removed = _storageRuntime.Storage.RemoveColumn(existingCollection.Name, columnName);
            if (!removed)
            {
                return StorageToolResult<SchemaChangeResult>.Fail(new StorageToolError("ColumnNotFound", "columnName", $"Column '{columnName}' was not found in '{existingCollection.Name}'."));
            }

            var updatedCollection = _storageRuntime.Storage.GetCollectionDefinition(existingCollection.Name)!;
            _logger.LogInformation("Schema change completed: DeleteColumn {CollectionName}.{ColumnName} v{SchemaVersion}", existingCollection.Name, columnName, GetSchemaVersion(updatedCollection));
            return StorageToolResult<SchemaChangeResult>.Ok(new SchemaChangeResult(existingCollection.Name, GetSchemaVersion(updatedCollection), "DeleteColumn"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schema change failed: DeleteColumn {CollectionName}.{ColumnName}", existingCollection.Name, columnName);
            return StorageToolResult<SchemaChangeResult>.Fail(new StorageToolError("SchemaChangeError", null, ex.Message));
        }
    }

    public StorageToolResult<RelationChangeResult> CreateRelation(
        string relationName,
        string relationType,
        string sourceCollection,
        string sourceColumn,
        string targetCollection,
        string targetColumn,
        string? description = null)
    {
        var schemaChangeGuard = EnsureSchemaChangesAllowed(nameof(CreateRelation));
        if (schemaChangeGuard is not null)
        {
            return StorageToolResult<RelationChangeResult>.Fail(schemaChangeGuard);
        }

        var nameErrors = new[]
        {
            ValidateName(relationName, "relationName", "relation"),
            ValidateName(sourceCollection, "sourceCollection", "collection"),
            ValidateName(sourceColumn, "sourceColumn", "column"),
            ValidateName(targetCollection, "targetCollection", "collection"),
            ValidateName(targetColumn, "targetColumn", "column")
        }.Where(error => error is not null).Cast<StorageToolError>().ToArray();
        if (nameErrors.Length > 0)
        {
            return StorageToolResult<RelationChangeResult>.Fail(nameErrors);
        }

        if (!Enum.TryParse<RelationType>(relationType, true, out var parsedRelationType))
        {
            return StorageToolResult<RelationChangeResult>.Fail(new StorageToolError(
                "InvalidRelationType",
                "relationType",
                $"Relation type '{relationType}' is invalid. Use OneToOne, OneToMany, ManyToOne, or ManyToMany."));
        }

        var impactGuard = EnsureImpactAnalysisCompleted(
            [SchemaChangeOperationType.AddRelation],
            sourceCollection,
            sourceColumn);
        if (impactGuard is not null)
        {
            return StorageToolResult<RelationChangeResult>.Fail(impactGuard);
        }

        try
        {
            _logger.LogInformation("Schema change started: CreateRelation {RelationName}", relationName);
            _storageRuntime.Storage.AddRelation(new RelationDefinition(
                relationName,
                parsedRelationType,
                sourceCollection,
                sourceColumn,
                targetCollection,
                targetColumn,
                description));

            var source = _storageRuntime.Storage.GetCollectionDefinition(sourceCollection);
            var target = _storageRuntime.Storage.GetCollectionDefinition(targetCollection);
            if (source is null || target is null)
            {
                return StorageToolResult<RelationChangeResult>.Fail(new StorageToolError("StorageError", null, "Relation created but one of involved collections could not be loaded."));
            }

            _logger.LogInformation("Schema change completed: CreateRelation {RelationName}", relationName);
            return StorageToolResult<RelationChangeResult>.Ok(new RelationChangeResult(
                relationName,
                source.Name,
                GetSchemaVersion(source),
                target.Name,
                GetSchemaVersion(target),
                "CreateRelation"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schema change failed: CreateRelation {RelationName}", relationName);
            return StorageToolResult<RelationChangeResult>.Fail(new StorageToolError("SchemaChangeError", null, ex.Message));
        }
    }

    public StorageToolResult<RelationChangeResult> DeleteRelation(string relationName)
    {
        var schemaChangeGuard = EnsureSchemaChangesAllowed(nameof(DeleteRelation));
        if (schemaChangeGuard is not null)
        {
            return StorageToolResult<RelationChangeResult>.Fail(schemaChangeGuard);
        }

        var invalidRelationNameError = ValidateName(relationName, "relationName", "relation");
        if (invalidRelationNameError is not null)
        {
            return StorageToolResult<RelationChangeResult>.Fail(invalidRelationNameError);
        }

        var existingRelation = _storageRuntime.Storage.GetRelation(relationName);
        if (existingRelation is null)
        {
            return StorageToolResult<RelationChangeResult>.Fail(new StorageToolError("RelationNotFound", "relationName", $"Relation '{relationName}' was not found."));
        }

        var impactGuard = EnsureImpactAnalysisCompleted(
            [SchemaChangeOperationType.RemoveRelation],
            existingRelation.SourceCollection,
            existingRelation.SourceColumn);
        if (impactGuard is not null)
        {
            return StorageToolResult<RelationChangeResult>.Fail(impactGuard);
        }

        try
        {
            _logger.LogInformation("Schema change started: DeleteRelation {RelationName}", relationName);
            var removed = _storageRuntime.Storage.RemoveRelation(relationName);
            if (!removed)
            {
                return StorageToolResult<RelationChangeResult>.Fail(new StorageToolError("RelationNotFound", "relationName", $"Relation '{relationName}' was not found."));
            }

            var source = _storageRuntime.Storage.GetCollectionDefinition(existingRelation.SourceCollection);
            var target = _storageRuntime.Storage.GetCollectionDefinition(existingRelation.TargetCollection);
            if (source is null || target is null)
            {
                return StorageToolResult<RelationChangeResult>.Fail(new StorageToolError("StorageError", null, "Relation removed but one of involved collections could not be loaded."));
            }

            _logger.LogInformation("Schema change completed: DeleteRelation {RelationName}", relationName);
            return StorageToolResult<RelationChangeResult>.Ok(new RelationChangeResult(
                relationName,
                source.Name,
                GetSchemaVersion(source),
                target.Name,
                GetSchemaVersion(target),
                "DeleteRelation"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schema change failed: DeleteRelation {RelationName}", relationName);
            return StorageToolResult<RelationChangeResult>.Fail(new StorageToolError("SchemaChangeError", null, ex.Message));
        }
    }

    public StorageToolResult<RecordResult> InsertRecord(string collectionName, Dictionary<string, string?> fields)
    {
        var invalidCollectionNameError = ValidateName(collectionName, "collectionName", "collection");
        if (invalidCollectionNameError is not null)
        {
            return StorageToolResult<RecordResult>.Fail(invalidCollectionNameError);
        }

        var collection = _storageRuntime.Storage.GetCollectionDefinition(collectionName);
        if (collection is null)
        {
            return StorageToolResult<RecordResult>.Fail(new StorageToolError("CollectionNotFound", "collectionName", $"Collection '{collectionName}' was not found."));
        }

        var fieldValues = new Dictionary<string, object?>();
        var errors = new List<StorageToolError>();
        foreach (var field in fields)
        {
            var column = collection.Columns.FirstOrDefault(c => string.Equals(c.Name, field.Key, StringComparison.OrdinalIgnoreCase));
            if (column is null)
            {
                errors.Add(new StorageToolError("ColumnNotFound", field.Key, $"Column '{field.Key}' was not found in '{collection.Name}'."));
                continue;
            }
            
            var fieldValue = ColumnValueMapper.ParseFromString(column.Type, field.Value);
            fieldValues[field.Key] = ColumnValueMapper.ToStorageValue(fieldValue);
        }
        if (errors.Count > 0)
        {
            return StorageToolResult<RecordResult>.Fail(errors);
        }

        try
        {
            var created = _storageRuntime.Storage.Create(collection.Name, fieldValues);
            return StorageToolResult<RecordResult>.Ok(MapRecord(collection, created));
        }
        catch (StorageValidationException ex)
        {
            _logger.LogInformation(ex, "InsertRecord validation failed for collection {CollectionName}.", collection.Name);
            return StorageToolResult<RecordResult>.Fail(ex.Errors.Select(MapStorageError).ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InsertRecord failed for collection {CollectionName}.", collection.Name);
            return StorageToolResult<RecordResult>.Fail(new StorageToolError("StorageError", null, ex.Message));
        }
    }

    public StorageToolResult<RecordResult> UpdateRecord(string collectionName, string recordId, Dictionary<string, string?> fields)
    {
        var invalidCollectionNameError = ValidateName(collectionName, "collectionName", "collection");
        if (invalidCollectionNameError is not null)
        {
            return StorageToolResult<RecordResult>.Fail(invalidCollectionNameError);
        }

        if (!Ulid.TryParse(recordId, out var id))
        {
            return StorageToolResult<RecordResult>.Fail(new StorageToolError("InvalidRecordId", "recordId", "Record ID must be a valid GUID."));
        }
        
        var collection = _storageRuntime.Storage.GetCollectionDefinition(collectionName);
        if (collection is null)
        {
            return StorageToolResult<RecordResult>.Fail(new StorageToolError("CollectionNotFound", "collectionName", $"Collection '{collectionName}' was not found."));
        }

        var existing = _storageRuntime.Storage.GetById(collection.Name, id);
        if (existing is null)
        {
            return StorageToolResult<RecordResult>.Fail(new StorageToolError("RecordNotFound", "recordId", $"Record '{recordId}' was not found in '{collection.Name}'."));
        }

        var errors = new List<StorageToolError>();
        var storageFields = new Dictionary<string, object?>(existing.Fields, StringComparer.Ordinal);
        foreach (var item in fields)
        {
            var column = collection.Columns.FirstOrDefault(c => string.Equals(c.Name, item.Key, StringComparison.OrdinalIgnoreCase));
            if (column is null)
            {
                errors.Add(new StorageToolError("ColumnNotFound", item.Key, $"Column '{item.Key}' was not found in '{collection.Name}'."));
                continue;
            }
            
            var fieldValue = ColumnValueMapper.ParseFromString(column.Type, item.Value);
            storageFields[column.Name] = ColumnValueMapper.ToStorageValue(fieldValue);
        }
        if (errors.Count > 0)
        {
            return StorageToolResult<RecordResult>.Fail(errors);
        }

        try
        {
            var updated = _storageRuntime.Storage.Update(new StorageRecord(id, collection.Name, storageFields));
            if (!updated)
            {
                return StorageToolResult<RecordResult>.Fail(new StorageToolError("RecordNotFound", "recordId", $"Record '{recordId}' was not found in '{collection.Name}'."));
            }

            var reloaded = _storageRuntime.Storage.GetById(collection.Name, id);
            if (reloaded is null)
            {
                return StorageToolResult<RecordResult>.Fail(new StorageToolError("StorageError", "recordId", "Record update completed but record could not be reloaded."));
            }

            return StorageToolResult<RecordResult>.Ok(MapRecord(collection, reloaded));
        }
        catch (StorageValidationException ex)
        {
            _logger.LogInformation(ex, "UpdateRecord validation failed for collection {CollectionName}, id {RecordId}.", collection.Name, recordId);
            return StorageToolResult<RecordResult>.Fail(ex.Errors.Select(MapStorageError).ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateRecord failed for collection {CollectionName}, id {RecordId}.", collection.Name, recordId);
            return StorageToolResult<RecordResult>.Fail(new StorageToolError("StorageError", null, ex.Message));
        }
    }

    public StorageToolResult<DeleteRecordResult> DeleteRecord(string collectionName, string recordId)
    {
        var invalidCollectionNameError = ValidateName(collectionName, "collectionName", "collection");
        if (invalidCollectionNameError is not null)
        {
            return StorageToolResult<DeleteRecordResult>.Fail(invalidCollectionNameError);
        }

        if (!Ulid.TryParse(recordId, out var id))
        {
            return StorageToolResult<DeleteRecordResult>.Fail(new StorageToolError("InvalidRecordId", "recordId", "Record ID must be a valid GUID."));
        }

        if (_storageRuntime.Storage.GetCollectionDefinition(collectionName) is null)
        {
            return StorageToolResult<DeleteRecordResult>.Fail(new StorageToolError("CollectionNotFound", "collectionName", $"Collection '{collectionName}' was not found."));
        }

        try
        {
            var deleted = _storageRuntime.Storage.Delete(collectionName, id);
            if (!deleted)
            {
                return StorageToolResult<DeleteRecordResult>.Fail(new StorageToolError("RecordNotFound", "recordId", $"Record '{recordId}' was not found in '{collectionName}'."));
            }

            return StorageToolResult<DeleteRecordResult>.Ok(new DeleteRecordResult(collectionName, recordId, true));
        }
        catch (StorageValidationException ex)
        {
            _logger.LogInformation(ex, "DeleteRecord validation failed for collection {CollectionName}, id {RecordId}.", collectionName, recordId);
            return StorageToolResult<DeleteRecordResult>.Fail(ex.Errors.Select(MapStorageError).ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteRecord failed for collection {CollectionName}, id {RecordId}.", collectionName, recordId);
            return StorageToolResult<DeleteRecordResult>.Fail(new StorageToolError("StorageError", null, ex.Message));
        }
    }

    private StorageToolResult<IReadOnlyCollection<StorageColumnDefinition>> BuildColumnDefinitions(IEnumerable<ColumnToolDefinition> columns)
    {
        var errors = new List<StorageToolError>();
        var result = new List<StorageColumnDefinition>();
        foreach (var column in columns)
        {
            var converted = BuildColumnDefinition(column);
            if (!converted.Success || converted.Data is null)
            {
                errors.AddRange(converted.Errors);
                continue;
            }

            result.Add(converted.Data);
        }

        return errors.Count > 0
            ? StorageToolResult<IReadOnlyCollection<StorageColumnDefinition>>.Fail(errors)
            : StorageToolResult<IReadOnlyCollection<StorageColumnDefinition>>.Ok(result);
    }

    private StorageToolResult<StorageColumnDefinition> BuildColumnDefinition(ColumnToolDefinition column)
    {
        var invalidNameError = ValidateName(column.Name, "column.name", "column");
        if (invalidNameError is not null)
        {
            return StorageToolResult<StorageColumnDefinition>.Fail(invalidNameError);
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(column.SemanticTypeName))
            {
                var semanticType = _semanticTypeRegistry.GetByNameOrAlias(column.SemanticTypeName);
                if (semanticType is null)
                {
                    return StorageToolResult<StorageColumnDefinition>.Fail(
                        new StorageToolError("SemanticTypeNotFound", "column.semanticTypeName", $"Semantic type '{column.SemanticTypeName}' is not registered."));
                }

                if (semanticType.BaseType != column.Type)
                {
                    return StorageToolResult<StorageColumnDefinition>.Fail(
                        new StorageToolError(
                            "SemanticTypeBaseTypeMismatch",
                            "column.semanticTypeName",
                            $"Semantic type '{semanticType.Name}' base type '{semanticType.BaseType}' is incompatible with column type '{column.Type}'."));
                }
            }

            var fieldValue = ColumnValueMapper.ParseFromString(column.Type, column.DefaultValue);
            return StorageToolResult<StorageColumnDefinition>.Ok(new StorageColumnDefinition(
                column.Name,
                column.Type,
                column.Description,
                column.Unique,
                column.ReadOnly,
                ColumnValueMapper.ToStorageValue(fieldValue),
                column.SemanticTypeName,
                column.ValidationPattern,
                column.ValidationPatterns));
        }
        catch (Exception ex)
        {
            return StorageToolResult<StorageColumnDefinition>.Fail(new StorageToolError("InvalidColumnDefinition", "column", ex.Message));
        }
    }

    private StorageToolError? EnsureSchemaChangesAllowed(string operationName)
    {
        if (_schemaToolOptions.AllowSchemaChanges)
        {
            return null;
        }

        _logger.LogWarning("Schema change rejected because schema changes are disabled. Operation: {Operation}", operationName);
        return new StorageToolError("SchemaChangesDisabled", "operation", $"Schema changes are disabled for '{operationName}'.");
    }

    private StorageToolError? EnsureImpactAnalysisCompleted(
        IReadOnlyCollection<SchemaChangeOperationType> operationTypes,
        string targetCollection,
        string? targetColumn)
    {
        var proposal = _schemaProposalStore.GetAll()
            .Where(item => operationTypes.Contains(item.OperationType))
            .Where(item => string.Equals(item.TargetCollection, targetCollection, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(targetColumn) ||
                           string.Equals(item.TargetColumn, targetColumn, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.CreatedUtc)
            .FirstOrDefault();
        if (proposal is null)
        {
            return new StorageToolError(
                "ImpactAnalysisRequired",
                "proposal",
                "Schema modification requires an analyzed schema change proposal.");
        }

        if (proposal.ImpactAnalysis is null)
        {
            return new StorageToolError(
                "ImpactAnalysisRequired",
                "proposal",
                $"Schema change proposal '{proposal.ProposalId}' must be analyzed before applying.");
        }

        return proposal.ImpactAnalysis.Classification switch
        {
            SchemaImpactClassification.Invalid => new StorageToolError(
                "InvalidImpactAnalysis",
                "proposal",
                proposal.ImpactAnalysis.Summary),
            SchemaImpactClassification.RequiresUserInstruction => new StorageToolError(
                "UserInstructionRequired",
                "proposal",
                proposal.ImpactAnalysis.Summary),
            _ => null
        };
    }

    private StorageToolError? ValidateProposalAgainstCurrentSchema(SchemaChangeOperationType operationType, string targetCollection, string? targetColumn)
    {
        var collection = _storageRuntime.Storage.GetCollectionDefinition(targetCollection);
        var targetColumnDefinition = collection?.Columns.FirstOrDefault(column =>
            string.Equals(column.Name, targetColumn, StringComparison.OrdinalIgnoreCase));

        return operationType switch
        {
            SchemaChangeOperationType.CreateCollection when collection is not null
                => new StorageToolError("CollectionAlreadyExists", "targetCollection", $"Collection '{targetCollection}' already exists."),
            SchemaChangeOperationType.RemoveCollection or SchemaChangeOperationType.RenameCollection or SchemaChangeOperationType.AddColumn
                when collection is null
                => new StorageToolError("CollectionNotFound", "targetCollection", $"Collection '{targetCollection}' was not found."),
            SchemaChangeOperationType.AddColumn when targetColumnDefinition is not null
                => new StorageToolError("ColumnAlreadyExists", "targetColumn", $"Column '{targetColumn}' already exists in '{targetCollection}'."),
            SchemaChangeOperationType.RemoveColumn or SchemaChangeOperationType.RenameColumn or SchemaChangeOperationType.ChangeColumnType or SchemaChangeOperationType.ChangeColumnDescription or SchemaChangeOperationType.ChangeSemanticType
                when targetColumnDefinition is null
                => new StorageToolError("ColumnNotFound", "targetColumn", $"Column '{targetColumn}' was not found in '{targetCollection}'."),
            SchemaChangeOperationType.AddRelation or SchemaChangeOperationType.RemoveRelation when collection is null
                => new StorageToolError("CollectionNotFound", "targetCollection", $"Collection '{targetCollection}' was not found."),
            _ => null
        };
    }

    private static bool RequiresTargetColumn(SchemaChangeOperationType operationType)
    {
        return operationType is
            SchemaChangeOperationType.AddColumn or
            SchemaChangeOperationType.RemoveColumn or
            SchemaChangeOperationType.RenameColumn or
            SchemaChangeOperationType.ChangeColumnType or
            SchemaChangeOperationType.ChangeColumnDescription or
            SchemaChangeOperationType.ChangeSemanticType;
    }

    private SchemaImpactResult BuildAffectedDataImpact(string targetCollection, string? targetColumn)
    {
        var collection = _storageRuntime.Storage.GetCollectionDefinition(targetCollection);
        if (collection is null)
        {
            return new SchemaImpactResult(
                0,
                "Collection does not exist yet; no persisted records are currently affected.",
                Array.Empty<string>());
        }

        var records = _storageRuntime.Storage.GetAll(targetCollection);
        if (string.IsNullOrWhiteSpace(targetColumn))
        {
            return new SchemaImpactResult(
                records.Count,
                $"Collection '{targetCollection}' has {records.Count} record(s).",
                records.Take(5).Select(record => record.Id.ToString()).ToArray());
        }

        var nonNullColumnValues = records.Count(record =>
            record.Fields.TryGetValue(targetColumn, out var value) && value is not null);
        return new SchemaImpactResult(
            nonNullColumnValues,
            $"Column '{targetColumn}' has {nonNullColumnValues} non-null value(s) in '{targetCollection}'.",
            records
                .Where(record => record.Fields.TryGetValue(targetColumn, out var value) && value is not null)
                .Take(5)
                .Select(record => record.Id.ToString())
                .ToArray());
    }

    private SchemaImpactResult BuildColumnsImpact(string targetCollection, string? targetColumn)
    {
        var collection = _storageRuntime.Storage.GetCollectionDefinition(targetCollection);
        if (collection is null)
        {
            return new SchemaImpactResult(0, $"Collection '{targetCollection}' does not exist.", Array.Empty<string>());
        }

        var columns = string.IsNullOrWhiteSpace(targetColumn)
            ? collection.Columns
            : collection.Columns.Where(column => string.Equals(column.Name, targetColumn, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new SchemaImpactResult(
            columns.Count,
            $"{columns.Count} column definition(s) may be affected.",
            columns.Select(column => $"{collection.Name}.{column.Name}:{column.Type}").ToArray());
    }

    private SchemaImpactResult BuildRelationsImpact(string targetCollection, string? targetColumn)
    {
        var relations = GetAffectedRelations(targetCollection, targetColumn);
        return new SchemaImpactResult(
            relations.Count,
            $"{relations.Count} relation(s) may be affected.",
            relations);
    }

    private SchemaImpactResult BuildSemanticTypesImpact(string targetCollection, string? targetColumn)
    {
        var collection = _storageRuntime.Storage.GetCollectionDefinition(targetCollection);
        if (collection is null)
        {
            return new SchemaImpactResult(0, $"Collection '{targetCollection}' does not exist.", Array.Empty<string>());
        }

        var semanticTypes = collection.Columns
            .Where(column =>
                (string.IsNullOrWhiteSpace(targetColumn) || string.Equals(column.Name, targetColumn, StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(column.SemanticTypeName))
            .Select(column => $"{column.Name}:{column.SemanticTypeName}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new SchemaImpactResult(
            semanticTypes.Length,
            $"{semanticTypes.Length} semantic type binding(s) may be affected.",
            semanticTypes);
    }

    private SchemaImpactResult BuildValidationRulesImpact(string targetCollection, string? targetColumn)
    {
        var affectedRules = GetAffectedRules(targetCollection, targetColumn).ToArray();
        return new SchemaImpactResult(
            affectedRules.Length,
            $"{affectedRules.Length} validation/rule binding(s) may be affected.",
            affectedRules);
    }

    private static SchemaImpactResult BuildDisplayRulesImpact(string targetCollection, string? targetColumn)
    {
        return new SchemaImpactResult(
            0,
            $"No display rule metadata registered for '{targetCollection}'{(string.IsNullOrWhiteSpace(targetColumn) ? string.Empty : $".{targetColumn}")}.",
            Array.Empty<string>());
    }

    private static SchemaImpactResult BuildSavedQueriesImpact(string targetCollection, string? targetColumn)
    {
        return new SchemaImpactResult(
            0,
            $"No saved query metadata registered for '{targetCollection}'{(string.IsNullOrWhiteSpace(targetColumn) ? string.Empty : $".{targetColumn}")}.",
            Array.Empty<string>());
    }

    /// <summary>
    /// Reports schema changes that are already held awaiting the user's
    /// confirmation and touch the same collection, since applying another
    /// change first could conflict with them.
    /// </summary>
    private SchemaImpactResult BuildPendingOperationsImpact(string targetCollection, string? targetColumn)
    {
        var pending = _schemaProposalStore.GetAll()
            .Where(proposal => string.Equals(proposal.TargetCollection, targetCollection, StringComparison.OrdinalIgnoreCase))
            .Where(proposal =>
                string.IsNullOrWhiteSpace(targetColumn) ||
                string.IsNullOrWhiteSpace(proposal.TargetColumn) ||
                string.Equals(proposal.TargetColumn, targetColumn, StringComparison.OrdinalIgnoreCase))
            .Select(proposal => $"{proposal.ProposalId}:{proposal.OperationType}")
            .ToArray();

        return new SchemaImpactResult(
            pending.Length,
            pending.Length == 0
                ? $"No schema changes are awaiting confirmation for '{targetCollection}'{(string.IsNullOrWhiteSpace(targetColumn) ? string.Empty : $".{targetColumn}")}."
                : $"{pending.Length} schema change(s) awaiting confirmation may conflict.",
            pending);
    }

    private static SchemaImpactResult BuildDependentToolsImpact(string targetCollection, string? targetColumn)
    {
        var collectionLevelTools = new[]
        {
            "GetCollectionSchema",
            "QueryRecords",
            "InsertRecord",
            "UpdateRecord",
            "DeleteRecord"
        };
        var columnLevelTools = string.IsNullOrWhiteSpace(targetColumn)
            ? Array.Empty<string>()
            : new[] { "QueryRecords", "InsertRecord", "UpdateRecord" };
        var tools = collectionLevelTools
            .Concat(columnLevelTools)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new SchemaImpactResult(
            tools.Length,
            $"{tools.Length} tool operation(s) depend on the affected schema surface.",
            tools);
    }

    private void ApplyOperationSpecificChecks(
        SchemaChangeProposal proposal,
        List<string> invalidReasons,
        List<string> instructionReasons)
    {
        var collection = _storageRuntime.Storage.GetCollectionDefinition(proposal.TargetCollection);
        var column = collection?.Columns.FirstOrDefault(item =>
            string.Equals(item.Name, proposal.TargetColumn, StringComparison.OrdinalIgnoreCase));

        switch (proposal.OperationType)
        {
            case SchemaChangeOperationType.CreateCollection when collection is not null:
                invalidReasons.Add($"Collection '{proposal.TargetCollection}' already exists.");
                break;
            case SchemaChangeOperationType.CreateCollection:
                if (proposal.Definition?.Columns is null || proposal.Definition.Columns.Count == 0)
                {
                    instructionReasons.Add("Provide at least one column definition in definition.columns for CreateCollection.");
                }
                else
                {
                    var duplicateColumns = proposal.Definition.Columns
                        .GroupBy(columnDefinition => columnDefinition.Name, StringComparer.OrdinalIgnoreCase)
                        .Where(group => group.Count() > 1)
                        .Select(group => group.Key)
                        .ToArray();
                    if (duplicateColumns.Length > 0)
                    {
                        invalidReasons.Add($"Duplicate column names are not allowed: {string.Join(", ", duplicateColumns)}.");
                    }

                    foreach (var columnDefinition in proposal.Definition.Columns)
                    {
                        var converted = BuildColumnDefinition(columnDefinition);
                        if (!converted.Success)
                        {
                            invalidReasons.AddRange(converted.Errors.Select(error => error.Message));
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(proposal.Definition?.CollectionDescription))
                {
                    instructionReasons.Add("Provide a collection description in definition.collectionDescription.");
                }

                if (proposal.Definition?.CollectionMetadata is null || proposal.Definition.CollectionMetadata.Count == 0)
                {
                    instructionReasons.Add("Provide descriptive collection metadata in definition.collectionMetadata for user and LLM analysis.");
                }

                break;
            case SchemaChangeOperationType.RemoveCollection when collection is null:
                invalidReasons.Add($"Collection '{proposal.TargetCollection}' does not exist.");
                break;
            case SchemaChangeOperationType.RenameCollection:
                if (collection is null)
                {
                    invalidReasons.Add($"Collection '{proposal.TargetCollection}' does not exist.");
                    break;
                }

                if (string.IsNullOrWhiteSpace(proposal.ProposedDefinition))
                {
                    instructionReasons.Add("Provide the new collection name in proposedDefinition.");
                    break;
                }

                var renameCollectionError = ValidateName(proposal.ProposedDefinition.Trim(), "proposedDefinition", "collection");
                if (renameCollectionError is not null)
                {
                    invalidReasons.Add(renameCollectionError.Message);
                    break;
                }

                var existingTarget = _storageRuntime.Storage.GetCollectionDefinition(proposal.ProposedDefinition.Trim());
                if (existingTarget is not null &&
                    !string.Equals(existingTarget.Name, proposal.TargetCollection, StringComparison.OrdinalIgnoreCase))
                {
                    invalidReasons.Add($"Collection '{proposal.ProposedDefinition.Trim()}' already exists.");
                }

                instructionReasons.Add("Collection rename requires an explicit migration operation implementation.");
                break;
            case SchemaChangeOperationType.AddColumn when collection is null:
                invalidReasons.Add($"Collection '{proposal.TargetCollection}' does not exist.");
                break;
            case SchemaChangeOperationType.AddColumn when column is not null:
                invalidReasons.Add($"Column '{proposal.TargetColumn}' already exists in '{proposal.TargetCollection}'.");
                break;
            case SchemaChangeOperationType.AddColumn:
                if (proposal.Definition?.Column is null)
                {
                    instructionReasons.Add("Provide the new column definition in definition.column for AddColumn.");
                    break;
                }

                var columnDefinitionResult = BuildColumnDefinition(proposal.Definition.Column);
                if (!columnDefinitionResult.Success)
                {
                    invalidReasons.AddRange(columnDefinitionResult.Errors.Select(error => error.Message));
                }

                if (!string.IsNullOrWhiteSpace(proposal.Definition.Column.DefaultValue))
                {
                    instructionReasons.Add("Adding a nullable column must not backfill existing records; omit definition.column.defaultValue.");
                }

                break;
            case SchemaChangeOperationType.RemoveColumn:
                if (collection is null)
                {
                    invalidReasons.Add($"Collection '{proposal.TargetCollection}' does not exist.");
                }
                else if (column is null)
                {
                    invalidReasons.Add($"Column '{proposal.TargetColumn}' does not exist in '{proposal.TargetCollection}'.");
                }

                if (collection is not null && column is not null)
                {
                    var recordsWithValue = _storageRuntime.Storage.GetAll(collection.Name)
                        .Count(record => record.Fields.TryGetValue(column.Name, out var value) && value is not null);
                    if (recordsWithValue > 0)
                    {
                        instructionReasons.Add(
                            $"Column '{proposal.TargetColumn}' contains {recordsWithValue} non-null value(s); choose how dependencies should be updated.");
                    }

                    var metadataReferences = CountApplicationMetadataReferences(collection, column.Name);
                    if (metadataReferences > 0)
                    {
                        instructionReasons.Add(
                            $"Application metadata contains {metadataReferences} reference(s) to '{proposal.TargetColumn}'.");
                    }
                }

                break;
            case SchemaChangeOperationType.RenameColumn:
                if (collection is null)
                {
                    invalidReasons.Add($"Collection '{proposal.TargetCollection}' does not exist.");
                    break;
                }

                if (column is null)
                {
                    invalidReasons.Add($"Column '{proposal.TargetColumn}' does not exist in '{proposal.TargetCollection}'.");
                    break;
                }

                if (string.IsNullOrWhiteSpace(proposal.ProposedDefinition))
                {
                    instructionReasons.Add("Provide the new column name in proposedDefinition.");
                    break;
                }

                var renameColumnError = ValidateName(proposal.ProposedDefinition.Trim(), "proposedDefinition", "column");
                if (renameColumnError is not null)
                {
                    invalidReasons.Add(renameColumnError.Message);
                    break;
                }

                if (collection.Columns.Any(existing =>
                        string.Equals(existing.Name, proposal.ProposedDefinition.Trim(), StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(existing.Name, column.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    invalidReasons.Add($"Column '{proposal.ProposedDefinition.Trim()}' already exists in '{proposal.TargetCollection}'.");
                }

                var targetName = proposal.Definition?.NewName?.Trim() ?? proposal.ProposedDefinition.Trim();
                if (!string.Equals(targetName, proposal.ProposedDefinition.Trim(), StringComparison.Ordinal))
                {
                    instructionReasons.Add("definition.newName and proposedDefinition disagree; provide one unambiguous target column name.");
                }

                var renameMetadataReferences = CountApplicationMetadataReferences(collection, column.Name);
                if (renameMetadataReferences > 0)
                {
                    instructionReasons.Add(
                        $"Application metadata contains {renameMetadataReferences} reference(s) to '{proposal.TargetColumn}'.");
                }

                break;
            case SchemaChangeOperationType.ChangeColumnType:
                if (collection is null)
                {
                    invalidReasons.Add($"Collection '{proposal.TargetCollection}' does not exist.");
                    break;
                }

                if (column is null)
                {
                    invalidReasons.Add($"Column '{proposal.TargetColumn}' does not exist in '{proposal.TargetCollection}'.");
                    break;
                }

                if (!Enum.TryParse<ColumnType>(proposal.ProposedDefinition.Trim(), true, out var newType))
                {
                    instructionReasons.Add("proposedDefinition must be a valid ColumnType value for ChangeColumnType.");
                    break;
                }

                var incompatibleRecords = _storageRuntime.Storage.GetAll(proposal.TargetCollection)
                    .Where(record => record.Fields.TryGetValue(column.Name, out var value) && value is not null)
                    .Where(record =>
                    {
                        var value = record.Fields[column.Name];
                        var converted = ColumnValueMapper.ToString(column.Type, ColumnValueMapper.FromStorageValue(value));
                        var parsed = ColumnValueMapper.ParseFromString(newType, converted);
                        return parsed is null;
                    })
                    .Take(3)
                    .Select(record => record.Id.ToString())
                    .ToArray();
                if (incompatibleRecords.Length > 0)
                {
                    invalidReasons.Add(
                        $"Existing values cannot be converted to '{newType}'. Example record IDs: {string.Join(", ", incompatibleRecords)}.");
                }

                break;
            case SchemaChangeOperationType.ChangeColumnDescription:
                if (collection is null)
                {
                    invalidReasons.Add($"Collection '{proposal.TargetCollection}' does not exist.");
                }
                else if (column is null)
                {
                    invalidReasons.Add($"Column '{proposal.TargetColumn}' does not exist in '{proposal.TargetCollection}'.");
                }

                if (string.IsNullOrWhiteSpace(proposal.ProposedDefinition))
                {
                    instructionReasons.Add("Provide the new column description in proposedDefinition.");
                }

                break;
            case SchemaChangeOperationType.ChangeSemanticType:
                if (collection is null)
                {
                    invalidReasons.Add($"Collection '{proposal.TargetCollection}' does not exist.");
                    break;
                }

                if (column is null)
                {
                    invalidReasons.Add($"Column '{proposal.TargetColumn}' does not exist in '{proposal.TargetCollection}'.");
                    break;
                }

                if (string.IsNullOrWhiteSpace(proposal.ProposedDefinition))
                {
                    instructionReasons.Add("Provide semantic type name in proposedDefinition.");
                    break;
                }

                var semanticType = _semanticTypeRegistry.GetByNameOrAlias(proposal.ProposedDefinition.Trim());
                if (semanticType is null)
                {
                    invalidReasons.Add($"Semantic type '{proposal.ProposedDefinition.Trim()}' is not registered.");
                    break;
                }

                if (semanticType.BaseType != column.Type)
                {
                    invalidReasons.Add(
                        $"Semantic type '{semanticType.Name}' base type '{semanticType.BaseType}' is incompatible with column type '{column.Type}'.");
                }

                break;
            case SchemaChangeOperationType.AddRelation:
            case SchemaChangeOperationType.RemoveRelation:
                if (collection is null)
                {
                    invalidReasons.Add($"Collection '{proposal.TargetCollection}' does not exist.");
                }

                if (string.IsNullOrWhiteSpace(proposal.ProposedDefinition))
                {
                    instructionReasons.Add("Provide relation details in proposedDefinition.");
                }

                break;
        }
    }

    private static SchemaImpactClassification ClassifyImpact(
        IReadOnlyCollection<string> invalidReasons,
        IReadOnlyCollection<string> instructionReasons,
        params SchemaImpactResult[] impacts)
    {
        if (invalidReasons.Count > 0)
        {
            return SchemaImpactClassification.Invalid;
        }

        if (instructionReasons.Count > 0)
        {
            return SchemaImpactClassification.RequiresUserInstruction;
        }

        var hasAffectedElements = impacts.Any(impact => impact.EstimatedCount > 0);
        return hasAffectedElements
            ? SchemaImpactClassification.RequiresConfirmation
            : SchemaImpactClassification.Safe;
    }

    private static string BuildImpactSummary(
        SchemaImpactClassification classification,
        IReadOnlyCollection<string> invalidReasons,
        IReadOnlyCollection<string> instructionReasons)
    {
        return classification switch
        {
            SchemaImpactClassification.Safe => "No affected system elements were detected.",
            SchemaImpactClassification.RequiresConfirmation => "Potential impacts were found; user confirmation is required before applying changes.",
            SchemaImpactClassification.RequiresUserInstruction => instructionReasons.FirstOrDefault() ?? "Additional user instruction is required.",
            SchemaImpactClassification.Invalid => invalidReasons.FirstOrDefault() ?? "Proposal is invalid.",
            _ => "Impact analysis completed."
        };
    }

    private IReadOnlyCollection<string> GetAffectedRelations(string targetCollection, string? targetColumn)
    {
        return _storageRuntime.Storage.GetRelations()
            .Where(relation =>
                string.Equals(relation.SourceCollection, targetCollection, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(relation.TargetCollection, targetCollection, StringComparison.OrdinalIgnoreCase))
            .Where(relation =>
                string.IsNullOrWhiteSpace(targetColumn) ||
                string.Equals(relation.SourceColumn, targetColumn, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(relation.TargetColumn, targetColumn, StringComparison.OrdinalIgnoreCase))
            .Select(relation => relation.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyCollection<string> GetAffectedRules(string targetCollection, string? targetColumn)
    {
        var collection = _storageRuntime.Storage.GetCollectionDefinition(targetCollection);
        if (collection is null)
        {
            return Array.Empty<string>();
        }

        var columns = string.IsNullOrWhiteSpace(targetColumn)
            ? collection.Columns
            : collection.Columns.Where(column => string.Equals(column.Name, targetColumn, StringComparison.OrdinalIgnoreCase)).ToArray();

        var rules = new List<string>();
        foreach (var column in columns)
        {
            if (column.Unique)
            {
                rules.Add($"{column.Name}:Unique");
            }

            if (column.ReadOnly)
            {
                rules.Add($"{column.Name}:ReadOnly");
            }

            if (column.DefaultValue is not null)
            {
                rules.Add($"{column.Name}:DefaultValue");
            }

            if (!string.IsNullOrWhiteSpace(column.SemanticTypeName))
            {
                rules.Add($"{column.Name}:SemanticType({column.SemanticTypeName})");
            }

            if (!string.IsNullOrWhiteSpace(column.ValidationPattern) || column.ValidationPatterns.Count > 0)
            {
                rules.Add($"{column.Name}:ValidationPattern");
            }
        }

        return rules
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool SupportsSchemaWorkflow(SchemaChangeOperationType operationType)
    {
        return operationType is
            SchemaChangeOperationType.CreateCollection or
            SchemaChangeOperationType.AddColumn or
            SchemaChangeOperationType.RemoveColumn or
            SchemaChangeOperationType.RenameColumn;
    }

    private IReadOnlyCollection<StorageToolError> ValidateSchemaWorkflowProposal(SchemaChangeProposal proposal)
    {
        var errors = new List<StorageToolError>();
        if (proposal.ImpactAnalysis is null)
        {
            errors.Add(new StorageToolError("ImpactAnalysisRequired", "proposal", "Impact analysis is required before starting schema workflow."));
            return errors;
        }

        if (proposal.ImpactAnalysis.Classification == SchemaImpactClassification.Invalid)
        {
            errors.Add(new StorageToolError("InvalidImpactAnalysis", "proposal", proposal.ImpactAnalysis.Summary));
        }

        switch (proposal.OperationType)
        {
            case SchemaChangeOperationType.CreateCollection:
                if (proposal.Definition?.Columns is null || proposal.Definition.Columns.Count == 0)
                {
                    errors.Add(new StorageToolError("InvalidDefinition", "proposal.definition.columns", "CreateCollection requires definition.columns."));
                }

                if (string.IsNullOrWhiteSpace(proposal.Definition?.CollectionDescription))
                {
                    errors.Add(new StorageToolError("InvalidDefinition", "proposal.definition.collectionDescription", "CreateCollection requires a descriptive collectionDescription."));
                }

                if (proposal.Definition?.CollectionMetadata is null || proposal.Definition.CollectionMetadata.Count == 0)
                {
                    errors.Add(new StorageToolError("InvalidDefinition", "proposal.definition.collectionMetadata", "CreateCollection requires descriptive collectionMetadata for user and LLM analysis."));
                }

                break;
            case SchemaChangeOperationType.AddColumn:
                if (proposal.Definition?.Column is null)
                {
                    errors.Add(new StorageToolError("InvalidDefinition", "proposal.definition.column", "AddColumn requires definition.column."));
                }
                else if (!string.IsNullOrWhiteSpace(proposal.TargetColumn) &&
                         !string.Equals(proposal.TargetColumn, proposal.Definition.Column.Name, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(new StorageToolError("InvalidDefinition", "proposal.definition.column.name", "definition.column.name must match targetColumn."));
                }

                break;
            case SchemaChangeOperationType.RenameColumn:
                var targetName = proposal.Definition?.NewName ?? proposal.ProposedDefinition;
                if (string.IsNullOrWhiteSpace(targetName))
                {
                    errors.Add(new StorageToolError("InvalidDefinition", "proposal.definition.newName", "RenameColumn requires newName or proposedDefinition."));
                }

                break;
        }

        return errors;
    }

    /// <summary>
    /// Decides whether a change needs the user's approval, and builds the
    /// decision to put to them. Returns null when the change is safe to apply
    /// straight away.
    /// </summary>
    private UserDecisionRequest? BuildWorkflowDecisionRequest(
        SchemaImpactAnalysis impact,
        string operationId,
        SchemaChangeProposal proposal)
    {
        if (impact.Classification == SchemaImpactClassification.Invalid)
        {
            return null;
        }

        var needsInstruction = impact.Classification == SchemaImpactClassification.RequiresUserInstruction;
        switch (proposal.OperationType)
        {
            case SchemaChangeOperationType.CreateCollection:
            {
                if (!needsInstruction && impact.Classification == SchemaImpactClassification.Safe)
                {
                    return null;
                }

                var actions = new List<WorkflowAction>
                {
                    new()
                    {
                        ActionId = "approve",
                        Title = "Approve",
                        Description = "Create the collection using this proposal.",
                        Decision = WorkflowDecision.Approve
                    },
                    new()
                    {
                        ActionId = "reject",
                        Title = "Reject",
                        Description = "Cancel this collection operation.",
                        Decision = WorkflowDecision.Reject
                    }
                };
                if (needsInstruction)
                {
                    actions.Add(new WorkflowAction
                    {
                        ActionId = "provide_instructions",
                        Title = "Provide Instructions",
                        Description = "Provide required metadata or adjustments before creating the collection.",
                        Decision = WorkflowDecision.ProvideInstructions
                    });
                }

                return new UserDecisionRequest
                {
                    OperationId = operationId,
                    Title = "Collection creation requires your decision",
                    Message = BuildCollectionDecisionMessage(proposal, impact),
                    AvailableActions = actions
                };
            }
            case SchemaChangeOperationType.AddColumn:
            {
                if (!needsInstruction && impact.Classification == SchemaImpactClassification.Safe)
                {
                    return null;
                }

                var actions = new List<WorkflowAction>
                {
                    new()
                    {
                        ActionId = "approve",
                        Title = "Approve",
                        Description = "Add the new nullable column and keep existing records unchanged.",
                        Decision = WorkflowDecision.Approve
                    },
                    new()
                    {
                        ActionId = "reject",
                        Title = "Reject",
                        Description = "Cancel this column addition.",
                        Decision = WorkflowDecision.Reject
                    }
                };
                if (needsInstruction)
                {
                    actions.Add(new WorkflowAction
                    {
                        ActionId = "provide_instructions",
                        Title = "Provide Instructions",
                        Description = "Provide additional instructions for a follow-up data processing operation.",
                        Decision = WorkflowDecision.ProvideInstructions
                    });
                }

                return new UserDecisionRequest
                {
                    OperationId = operationId,
                    Title = "Column addition requires your decision",
                    Message = BuildAddColumnDecisionMessage(proposal, impact),
                    AvailableActions = actions
                };
            }
            case SchemaChangeOperationType.RemoveColumn:
                var removeCollection = _storageRuntime.Storage.GetCollectionDefinition(proposal.TargetCollection);
                var removeMetadataReferences =
                    removeCollection is null || string.IsNullOrWhiteSpace(proposal.TargetColumn)
                        ? 0
                        : CountApplicationMetadataReferences(removeCollection, proposal.TargetColumn);
                return new UserDecisionRequest
                {
                    OperationId = operationId,
                    Title = "Column removal requires your decision",
                    Message = BuildRemoveColumnDecisionMessage(proposal, impact, removeMetadataReferences),
                    AvailableActions =
                    [
                        new WorkflowAction
                        {
                            ActionId = "remove_update_dependencies",
                            Title = "Remove and update dependencies",
                            Description = "Remove the column and retain affected artifacts by updating references where possible.",
                            Decision = WorkflowDecision.Approve
                        },
                        new WorkflowAction
                        {
                            ActionId = "remove_delete_dependencies",
                            Title = "Remove and delete affected dependencies",
                            Description = "Remove the column and delete affected queries/rules that cannot be updated.",
                            Decision = WorkflowDecision.Approve
                        },
                        new WorkflowAction
                        {
                            ActionId = "cancel",
                            Title = "Cancel operation",
                            Description = "Do not remove this column.",
                            Decision = WorkflowDecision.Reject
                        },
                        new WorkflowAction
                        {
                            ActionId = "provide_instructions",
                            Title = "Provide custom instructions",
                            Description = "Specify exactly how dependent artifacts should be handled.",
                            Decision = WorkflowDecision.ProvideInstructions
                        }
                    ]
                };
            case SchemaChangeOperationType.RenameColumn:
            {
                var hasAmbiguousReferences =
                    impact.SavedQueries.EstimatedCount > 0 ||
                    impact.DisplayRules.EstimatedCount > 0 ||
                    impact.PendingOperations.EstimatedCount > 0;
                var renameCollection = _storageRuntime.Storage.GetCollectionDefinition(proposal.TargetCollection);
                var metadataReferences =
                    renameCollection is null || string.IsNullOrWhiteSpace(proposal.TargetColumn)
                        ? 0
                        : CountApplicationMetadataReferences(renameCollection, proposal.TargetColumn);
                if (!needsInstruction && !hasAmbiguousReferences)
                {
                    return null;
                }

                return new UserDecisionRequest
                {
                    OperationId = operationId,
                    Title = "Column rename requires your decision",
                    Message = BuildRenameColumnDecisionMessage(proposal, impact, metadataReferences),
                    AvailableActions =
                    [
                        new WorkflowAction
                        {
                            ActionId = "approve_auto_update",
                            Title = "Approve automatic reference updates",
                            Description = "Rename the column and update deterministic references automatically.",
                            Decision = WorkflowDecision.Approve
                        },
                        new WorkflowAction
                        {
                            ActionId = "reject",
                            Title = "Reject",
                            Description = "Cancel this column rename operation.",
                            Decision = WorkflowDecision.Reject
                        },
                        new WorkflowAction
                        {
                            ActionId = "provide_instructions",
                            Title = "Provide custom instructions",
                            Description = "Describe how references should be handled before renaming.",
                            Decision = WorkflowDecision.ProvideInstructions
                        }
                    ]
                };
            }
            default:
                return null;
        }
    }

    private string ApplySchemaWorkflowChange(SchemaChangeProposal proposal)
    {
        switch (proposal.OperationType)
        {
            case SchemaChangeOperationType.CreateCollection:
            {
                var definition = proposal.Definition ?? throw new InvalidOperationException("CreateCollection requires a structured definition.");
                if (definition.Columns is null || definition.Columns.Count == 0)
                {
                    throw new InvalidOperationException("CreateCollection requires definition.columns.");
                }

                var columns = BuildColumnDefinitions(definition.Columns);
                if (!columns.Success || columns.Data is null)
                {
                    throw new InvalidOperationException(string.Join("; ", columns.Errors.Select(error => error.Message)));
                }

                var description = string.IsNullOrWhiteSpace(definition.CollectionDescription)
                    ? proposal.ProposedDefinition
                    : definition.CollectionDescription.Trim();
                var metadata = new Dictionary<string, string?>(definition.CollectionMetadata ?? new Dictionary<string, string?>(), StringComparer.Ordinal)
                {
                    ["llmSummary"] = proposal.Reason
                };
                if (!metadata.ContainsKey("createdBy"))
                {
                    metadata["createdBy"] = proposal.Source;
                }

                _storageRuntime.Storage.CreateCollection(
                    new CollectionDefinition(
                        proposal.TargetCollection,
                        description,
                        columns.Data,
                        metadata));

                return $"Collection '{proposal.TargetCollection}' created with metadata and {columns.Data.Count} column(s).";
            }
            case SchemaChangeOperationType.AddColumn:
            {
                var definition = proposal.Definition ?? throw new InvalidOperationException("AddColumn requires a structured definition.");
                var requestedColumn = definition.Column ?? throw new InvalidOperationException("AddColumn requires definition.column.");
                if (!string.IsNullOrWhiteSpace(requestedColumn.DefaultValue))
                {
                    throw new InvalidOperationException("AddColumn workflow does not allow defaultValue because existing records must remain without a value.");
                }

                var normalizedColumn = requestedColumn with { DefaultValue = null };
                var columnResult = BuildColumnDefinition(normalizedColumn);
                if (!columnResult.Success || columnResult.Data is null)
                {
                    throw new InvalidOperationException(string.Join("; ", columnResult.Errors.Select(error => error.Message)));
                }

                _storageRuntime.Storage.AddColumn(proposal.TargetCollection, columnResult.Data);
                var existingRecords = _storageRuntime.Storage.GetAll(proposal.TargetCollection).Count;
                return $"Column '{columnResult.Data.Name}' added. Existing records ({existingRecords}) remain without values for this column.";
            }
            case SchemaChangeOperationType.RemoveColumn:
            {
                if (string.IsNullOrWhiteSpace(proposal.TargetColumn))
                {
                    throw new InvalidOperationException("RemoveColumn requires targetColumn.");
                }

                var removed = _storageRuntime.Storage.RemoveColumn(proposal.TargetCollection, proposal.TargetColumn);
                if (!removed)
                {
                    throw new InvalidOperationException($"Column '{proposal.TargetColumn}' was not found in '{proposal.TargetCollection}'.");
                }

                return $"Column '{proposal.TargetCollection}.{proposal.TargetColumn}' removed.";
            }
            case SchemaChangeOperationType.RenameColumn:
            {
                if (string.IsNullOrWhiteSpace(proposal.TargetColumn))
                {
                    throw new InvalidOperationException("RenameColumn requires targetColumn.");
                }

                var collection = _storageRuntime.Storage.GetCollectionDefinition(proposal.TargetCollection)
                                 ?? throw new InvalidOperationException($"Collection '{proposal.TargetCollection}' was not found.");
                var existing = collection.Columns.FirstOrDefault(column =>
                    string.Equals(column.Name, proposal.TargetColumn, StringComparison.OrdinalIgnoreCase))
                               ?? throw new InvalidOperationException($"Column '{proposal.TargetColumn}' was not found in '{proposal.TargetCollection}'.");
                var newName = proposal.Definition?.NewName?.Trim() ?? proposal.ProposedDefinition.Trim();
                var updated = new StorageColumnDefinition(
                    newName,
                    existing.Type,
                    existing.Description,
                    existing.Unique,
                    existing.ReadOnly,
                    existing.DefaultValue,
                    existing.SemanticTypeName,
                    existing.ValidationPattern,
                    existing.ValidationPatterns);

                var renamed = _storageRuntime.Storage.UpdateColumn(proposal.TargetCollection, existing.Name, updated);
                if (!renamed)
                {
                    throw new InvalidOperationException($"Column '{proposal.TargetColumn}' could not be renamed in '{proposal.TargetCollection}'.");
                }

                return $"Column renamed: {proposal.TargetCollection}.{existing.Name} -> {proposal.TargetCollection}.{newName}.";
            }
            default:
                throw new InvalidOperationException($"Operation '{proposal.OperationType}' is not supported in schema workflow execution.");
        }
    }

    private static string BuildCollectionDecisionMessage(SchemaChangeProposal proposal, SchemaImpactAnalysis impact)
    {
        var metadataCount = proposal.Definition?.CollectionMetadata?.Count ?? 0;
        var columnCount = proposal.Definition?.Columns?.Count ?? 0;
        return
            $"Workflow requires your decision.{Environment.NewLine}{Environment.NewLine}" +
            $"Operation:{Environment.NewLine}Create collection '{proposal.TargetCollection}'{Environment.NewLine}{Environment.NewLine}" +
            $"Impact:{Environment.NewLine}" +
            $"- Existing records affected: {impact.ExistingRecords.EstimatedCount}{Environment.NewLine}" +
            $"- Relations potentially affected: {impact.Relations.EstimatedCount}{Environment.NewLine}" +
            $"- Semantic bindings affected: {impact.SemanticTypes.EstimatedCount}{Environment.NewLine}" +
            $"- Collection metadata entries: {metadataCount}{Environment.NewLine}" +
            $"- Proposed columns: {columnCount}{Environment.NewLine}{Environment.NewLine}" +
            $"Summary:{Environment.NewLine}{impact.Summary}";
    }

    private static string BuildAddColumnDecisionMessage(SchemaChangeProposal proposal, SchemaImpactAnalysis impact)
    {
        var targetColumn = proposal.TargetColumn ?? proposal.Definition?.Column?.Name ?? "Unknown";
        return
            $"Workflow requires your decision.{Environment.NewLine}{Environment.NewLine}" +
            $"Operation:{Environment.NewLine}Add column '{proposal.TargetCollection}.{targetColumn}'{Environment.NewLine}{Environment.NewLine}" +
            $"Impact:{Environment.NewLine}" +
            $"- Existing records in collection: {impact.ExistingRecords.EstimatedCount}{Environment.NewLine}" +
            $"- Display rules affected: {impact.DisplayRules.EstimatedCount}{Environment.NewLine}" +
            $"- Validation rules affected: {impact.ValidationRules.EstimatedCount}{Environment.NewLine}" +
            $"- Saved queries affected: {impact.SavedQueries.EstimatedCount}{Environment.NewLine}{Environment.NewLine}" +
            $"Existing records remain without a value for the new nullable column.{Environment.NewLine}{Environment.NewLine}" +
            $"Summary:{Environment.NewLine}{impact.Summary}";
    }

    private static string BuildRemoveColumnDecisionMessage(
        SchemaChangeProposal proposal,
        SchemaImpactAnalysis impact,
        int metadataReferences)
    {
        return
            $"Workflow requires your decision.{Environment.NewLine}{Environment.NewLine}" +
            $"Operation:{Environment.NewLine}Remove column '{proposal.TargetCollection}.{proposal.TargetColumn}'{Environment.NewLine}{Environment.NewLine}" +
            $"Impact:{Environment.NewLine}" +
            $"- {impact.ExistingRecords.EstimatedCount} records contain values{Environment.NewLine}" +
            $"- {impact.SavedQueries.EstimatedCount} saved queries depend on this column{Environment.NewLine}" +
            $"- {impact.DisplayRules.EstimatedCount} display rules depend on this column{Environment.NewLine}" +
            $"- {impact.ValidationRules.EstimatedCount} validation rules depend on this column{Environment.NewLine}" +
            $"- {metadataReferences} application metadata references depend on this column{Environment.NewLine}" +
            $"- {impact.Relations.EstimatedCount} relations reference this column{Environment.NewLine}" +
            $"- {impact.PendingOperations.EstimatedCount} pending operations may conflict{Environment.NewLine}{Environment.NewLine}" +
            "Choose how to proceed.";
    }

    private static string BuildRenameColumnDecisionMessage(
        SchemaChangeProposal proposal,
        SchemaImpactAnalysis impact,
        int metadataReferences)
    {
        var newName = proposal.Definition?.NewName?.Trim() ?? proposal.ProposedDefinition.Trim();
        return
            $"Workflow requires your decision.{Environment.NewLine}{Environment.NewLine}" +
            $"Operation:{Environment.NewLine}Rename column '{proposal.TargetCollection}.{proposal.TargetColumn}' to '{proposal.TargetCollection}.{newName}'{Environment.NewLine}{Environment.NewLine}" +
            $"Affected references:{Environment.NewLine}" +
            $"- Saved queries: {impact.SavedQueries.EstimatedCount}{Environment.NewLine}" +
            $"- Display rules: {impact.DisplayRules.EstimatedCount}{Environment.NewLine}" +
            $"- Validation rules: {impact.ValidationRules.EstimatedCount}{Environment.NewLine}" +
            $"- Semantic rules: {impact.SemanticTypes.EstimatedCount}{Environment.NewLine}" +
            $"- Application metadata references: {metadataReferences}{Environment.NewLine}" +
            $"- Relations: {impact.Relations.EstimatedCount}{Environment.NewLine}" +
            $"- Pending operations: {impact.PendingOperations.EstimatedCount}{Environment.NewLine}{Environment.NewLine}" +
            $"Summary:{Environment.NewLine}{impact.Summary}";
    }

    private static int CountApplicationMetadataReferences(CollectionDefinition collection, string columnName)
    {
        if (collection.Metadata.Count == 0)
        {
            return 0;
        }

        return collection.Metadata.Values.Count(value =>
            !string.IsNullOrWhiteSpace(value) &&
            value.Contains(columnName, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyCollection<StorageToolError> ValidateDataQueryDefinition(
        DataQueryDefinition definition,
        CollectionDefinition collection)
    {
        var errors = new List<StorageToolError>();
        var columns = collection.Columns
            .ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        var selectColumns = definition.SelectColumns ?? [];
        foreach (var columnName in selectColumns)
        {
            if (!columns.ContainsKey(columnName))
            {
                errors.Add(new StorageToolError("ColumnNotFound", "selectColumns", $"Column '{columnName}' was not found in '{collection.Name}'."));
            }
        }

        var groupByColumns = definition.GroupByColumns ?? [];
        foreach (var groupBy in groupByColumns)
        {
            if (!columns.ContainsKey(groupBy))
            {
                errors.Add(new StorageToolError("ColumnNotFound", "groupByColumns", $"Column '{groupBy}' was not found in '{collection.Name}'."));
            }
        }

        switch (definition.QueryType)
        {
            case DataQueryType.MostFrequent:
            case DataQueryType.FindDuplicates:
                if (groupByColumns.Count == 0)
                {
                    errors.Add(new StorageToolError("InvalidGroupBy", "groupByColumns", $"{definition.QueryType} requires one or more groupByColumns."));
                }

                break;
            case DataQueryType.FindUnreferenced:
                if (string.IsNullOrWhiteSpace(definition.RelatedCollectionName))
                {
                    errors.Add(new StorageToolError("InvalidRelatedCollection", "relatedCollectionName", "FindUnreferenced requires relatedCollectionName."));
                    break;
                }

                var relatedCollection = _storageRuntime.Storage.GetCollectionDefinition(definition.RelatedCollectionName);
                if (relatedCollection is null)
                {
                    errors.Add(new StorageToolError("CollectionNotFound", "relatedCollectionName", $"Collection '{definition.RelatedCollectionName}' was not found."));
                    break;
                }

                if (string.IsNullOrWhiteSpace(definition.CollectionKeyColumn))
                {
                    errors.Add(new StorageToolError("InvalidCollectionKey", "collectionKeyColumn", "FindUnreferenced requires collectionKeyColumn."));
                }
                else if (!columns.TryGetValue(definition.CollectionKeyColumn, out var primaryColumn))
                {
                    errors.Add(new StorageToolError("ColumnNotFound", "collectionKeyColumn", $"Column '{definition.CollectionKeyColumn}' was not found in '{collection.Name}'."));
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(definition.RelatedKeyColumn))
                    {
                        errors.Add(new StorageToolError("InvalidRelatedKey", "relatedKeyColumn", "FindUnreferenced requires relatedKeyColumn."));
                    }
                    else
                    {
                        var relatedColumn = relatedCollection.Columns.FirstOrDefault(column =>
                            string.Equals(column.Name, definition.RelatedKeyColumn, StringComparison.OrdinalIgnoreCase));
                        if (relatedColumn is null)
                        {
                            errors.Add(new StorageToolError("ColumnNotFound", "relatedKeyColumn", $"Column '{definition.RelatedKeyColumn}' was not found in '{relatedCollection.Name}'."));
                        }
                        else if (relatedColumn.Type != primaryColumn.Type)
                        {
                            errors.Add(new StorageToolError(
                                "IncompatibleColumnTypes",
                                "relatedKeyColumn",
                                $"Columns '{collection.Name}.{primaryColumn.Name}' and '{relatedCollection.Name}.{relatedColumn.Name}' have incompatible types."));
                        }
                    }
                }

                break;
        }

        return errors;
    }

    private DataQueryDefinition NormalizeDataQueryDefinition(
        DataQueryDefinition definition,
        CollectionDefinition collection)
    {
        string? NormalizeColumn(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var column = collection.Columns.FirstOrDefault(item => string.Equals(item.Name, value, StringComparison.OrdinalIgnoreCase));
            return column?.Name ?? value.Trim();
        }

        var groupBy = (definition.GroupByColumns ?? [])
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(item => NormalizeColumn(item)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var selectColumns = (definition.SelectColumns ?? [])
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(item => NormalizeColumn(item)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        string? normalizedRelatedKeyColumn = null;
        if (!string.IsNullOrWhiteSpace(definition.RelatedCollectionName) &&
            !string.IsNullOrWhiteSpace(definition.RelatedKeyColumn))
        {
            var relatedCollection = _storageRuntime.Storage.GetCollectionDefinition(definition.RelatedCollectionName.Trim());
            var relatedColumn = relatedCollection?.Columns.FirstOrDefault(column =>
                string.Equals(column.Name, definition.RelatedKeyColumn, StringComparison.OrdinalIgnoreCase));
            normalizedRelatedKeyColumn = relatedColumn?.Name ?? definition.RelatedKeyColumn.Trim();
        }

        var normalizedRelatedCollectionName = string.IsNullOrWhiteSpace(definition.RelatedCollectionName)
            ? null
            : definition.RelatedCollectionName.Trim();
        var normalizedCollectionKeyColumn = NormalizeColumn(definition.CollectionKeyColumn);
        if (definition.QueryType == DataQueryType.FindUnreferenced &&
            !string.IsNullOrWhiteSpace(normalizedRelatedCollectionName) &&
            (string.IsNullOrWhiteSpace(normalizedCollectionKeyColumn) || string.IsNullOrWhiteSpace(normalizedRelatedKeyColumn)))
        {
            var relation = _storageRuntime.Storage.GetRelations()
                .FirstOrDefault(item =>
                    (string.Equals(item.SourceCollection, collection.Name, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(item.TargetCollection, normalizedRelatedCollectionName, StringComparison.OrdinalIgnoreCase)) ||
                    (string.Equals(item.TargetCollection, collection.Name, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(item.SourceCollection, normalizedRelatedCollectionName, StringComparison.OrdinalIgnoreCase)));
            if (relation is not null)
            {
                if (string.Equals(relation.SourceCollection, collection.Name, StringComparison.OrdinalIgnoreCase))
                {
                    normalizedCollectionKeyColumn ??= relation.SourceColumn;
                    normalizedRelatedKeyColumn ??= relation.TargetColumn;
                }
                else
                {
                    normalizedCollectionKeyColumn ??= relation.TargetColumn;
                    normalizedRelatedKeyColumn ??= relation.SourceColumn;
                }
            }
        }

        return definition with
        {
            CollectionName = collection.Name,
            CollectionKeyColumn = normalizedCollectionKeyColumn,
            GroupByColumns = groupBy,
            SelectColumns = selectColumns,
            RelatedCollectionName = normalizedRelatedCollectionName,
            RelatedKeyColumn = normalizedRelatedKeyColumn
        };
    }

    private static IEnumerable<string> BuildSemanticHints(CollectionDefinition collection, DataQueryDefinition definition)
    {
        var selectedColumns = (definition.SelectColumns ?? [])
            .Concat(definition.GroupByColumns ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var columnName in selectedColumns)
        {
            var column = collection.Columns.FirstOrDefault(item => string.Equals(item.Name, columnName, StringComparison.OrdinalIgnoreCase));
            if (column is null || string.IsNullOrWhiteSpace(column.SemanticTypeName))
            {
                continue;
            }

            yield return $"{collection.Name}.{column.Name} semantic type: {column.SemanticTypeName}";
        }
    }

    private static string BuildDataQuerySummary(DataQueryDefinition definition)
    {
        return definition.QueryType switch
        {
            DataQueryType.MostFrequent => $"Group '{definition.CollectionName}' by [{string.Join(", ", definition.GroupByColumns ?? [])}] and return top {definition.Limit} by frequency.",
            DataQueryType.FindDuplicates => $"Find duplicate values in '{definition.CollectionName}' by [{string.Join(", ", definition.GroupByColumns ?? [])}].",
            DataQueryType.FindUnreferenced => $"Find '{definition.CollectionName}' records where '{definition.CollectionKeyColumn}' is not referenced by '{definition.RelatedCollectionName}.{definition.RelatedKeyColumn}'.",
            _ => "Data query."
        };
    }

    private IReadOnlyCollection<DataQueryRow> ExecuteDataQuery(
        DataQueryDefinition definition,
        CollectionDefinition collection)
    {
        var records = _storageRuntime.Storage.GetAll(collection.Name).ToArray();
        return definition.QueryType switch
        {
            DataQueryType.MostFrequent => ExecuteMostFrequent(collection, records, definition),
            DataQueryType.FindDuplicates => ExecuteFindDuplicates(collection, records, definition),
            DataQueryType.FindUnreferenced => ExecuteFindUnreferenced(collection, records, definition),
            _ => throw new InvalidOperationException($"Unsupported query type '{definition.QueryType}'.")
        };
    }

    private static IReadOnlyCollection<DataQueryRow> ExecuteMostFrequent(
        CollectionDefinition collection,
        IReadOnlyCollection<StorageRecord> records,
        DataQueryDefinition definition)
    {
        var keyColumns = definition.GroupByColumns ?? [];
        return records
            .GroupBy(record => BuildGroupKey(record, keyColumns))
            .Select(group => new { group.Key, Count = group.Count() })
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .Take(definition.Limit)
            .Select(item =>
            {
                var fields = ParseGroupKey(item.Key, keyColumns);
                fields["Count"] = item.Count.ToString(CultureInfo.InvariantCulture);
                return new DataQueryRow(fields);
            })
            .ToArray();
    }

    private static IReadOnlyCollection<DataQueryRow> ExecuteFindDuplicates(
        CollectionDefinition collection,
        IReadOnlyCollection<StorageRecord> records,
        DataQueryDefinition definition)
    {
        var keyColumns = definition.GroupByColumns ?? [];
        return records
            .GroupBy(record => BuildGroupKey(record, keyColumns))
            .Select(group => new
            {
                group.Key,
                Count = group.Count(),
                SampleRecordIds = group.Take(5).Select(record => record.Id.ToString()).ToArray()
            })
            .Where(item => item.Count > 1)
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .Take(definition.Limit)
            .Select(item =>
            {
                var fields = ParseGroupKey(item.Key, keyColumns);
                fields["DuplicateCount"] = item.Count.ToString(CultureInfo.InvariantCulture);
                fields["SampleRecordIds"] = string.Join(", ", item.SampleRecordIds);
                return new DataQueryRow(fields);
            })
            .ToArray();
    }

    private IReadOnlyCollection<DataQueryRow> ExecuteFindUnreferenced(
        CollectionDefinition collection,
        IReadOnlyCollection<StorageRecord> records,
        DataQueryDefinition definition)
    {
        var relatedCollection = _storageRuntime.Storage.GetCollectionDefinition(definition.RelatedCollectionName!)
                                ?? throw new InvalidOperationException($"Collection '{definition.RelatedCollectionName}' was not found.");
        var relatedRecords = _storageRuntime.Storage.GetAll(relatedCollection.Name);
        var relatedKeyValues = relatedRecords
            .Select(record => record.Fields.TryGetValue(definition.RelatedKeyColumn!, out var value) ? value : null)
            .Where(value => value is not null)
            .ToHashSet();

        var unreferenced = records
            .Where(record => record.Fields.TryGetValue(definition.CollectionKeyColumn!, out var value) && value is not null)
            .Where(record =>
            {
                record.Fields.TryGetValue(definition.CollectionKeyColumn!, out var value);
                return !relatedKeyValues.Contains(value);
            })
            .Take(definition.Limit)
            .ToArray();
        return unreferenced
            .Select(record => ToDataQueryRow(collection, record, definition.SelectColumns))
            .ToArray();
    }

    private static DataQueryRow ToDataQueryRow(
        CollectionDefinition collection,
        StorageRecord record,
        IReadOnlyCollection<string>? selectedColumns)
    {
        var columnTypes = collection.Columns.ToDictionary(column => column.Name, column => column.Type, StringComparer.OrdinalIgnoreCase);
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        var projection = selectedColumns is null || selectedColumns.Count == 0
            ? collection.Columns.Select(column => column.Name)
            : selectedColumns;
        foreach (var fieldName in projection)
        {
            if (!record.Fields.TryGetValue(fieldName, out var value))
            {
                fields[fieldName] = null;
                continue;
            }

            var type = columnTypes.TryGetValue(fieldName, out var columnType) ? columnType : ColumnType.String;
            fields[fieldName] = ColumnValueMapper.ToString(type, ColumnValueMapper.FromStorageValue(value));
        }

        fields["Id"] = record.Id.ToString();
        return new DataQueryRow(fields);
    }

    private static string BuildGroupKey(StorageRecord record, IReadOnlyCollection<string> keyColumns)
    {
        var values = keyColumns.Select(column =>
        {
            record.Fields.TryGetValue(column, out var value);
            return value?.ToString() ?? string.Empty;
        });
        return string.Join("||", values);
    }

    private static Dictionary<string, string?> ParseGroupKey(string groupKey, IReadOnlyCollection<string> keyColumns)
    {
        var segments = groupKey.Split("||");
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        var index = 0;
        foreach (var column in keyColumns)
        {
            fields[column] = index < segments.Length ? segments[index] : null;
            index++;
        }

        return fields;
    }

    private static StorageToolError? ValidateName(string value, string fieldName, string nameType)
    {
        // The caller passes a lowercase noun for the message; the error code is
        // PascalCase, so it does not read as "InvalidcolumnName".
        var code = $"Invalid{char.ToUpperInvariant(nameType[0])}{nameType[1..]}Name";

        if (string.IsNullOrWhiteSpace(value))
        {
            return new StorageToolError(code, fieldName, $"{nameType} name is required.");
        }

        if (!NameRegex().IsMatch(value))
        {
            return new StorageToolError(code, fieldName, $"{nameType} name '{value}' is invalid. Use letters, digits, and underscores, and start with a letter.");
        }

        return null;
    }

    private CollectionSchemaResult MapCollectionSchema(CollectionDefinition collection)
    {
        var relations = _storageRuntime.Storage.GetRelations()
            .Where(relation =>
                string.Equals(relation.SourceCollection, collection.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(relation.TargetCollection, collection.Name, StringComparison.OrdinalIgnoreCase))
            .Select(relation => new RelationSchemaResult(
                relation.Name,
                relation.Type.ToString(),
                relation.SourceCollection,
                relation.SourceColumn,
                relation.TargetCollection,
                relation.TargetColumn,
                relation.Description))
            .ToArray();
        var columns = collection.Columns
            .Select(column =>
            {
                var fieldValue = ColumnValueMapper.FromStorageValue(column.DefaultValue);
                return new ColumnSchemaResult(
                    column.Name,
                    column.Type,
                    column.Description,
                    column.Unique,
                    column.ReadOnly,
                    ColumnValueMapper.ToString(column.Type, fieldValue),
                    column.SemanticTypeName,
                    column.ValidationPattern,
                    column.ValidationPatterns.ToArray());
            })
            .ToArray();

        return new CollectionSchemaResult(
            collection.Name,
            collection.Description,
            GetSchemaVersion(collection),
            columns,
            relations);
    }

    private static int GetSchemaVersion(CollectionDefinition collection)
    {
        if (collection.Metadata.TryGetValue(MemoryStorage.SchemaVersionMetadataKey, out var version) &&
            int.TryParse(version, out var parsedVersion) &&
            parsedVersion > 0)
        {
            return parsedVersion;
        }

        return 1;
    }

    private static StorageToolError MapStorageError(StorageValidationError error)
    {
        return new StorageToolError(error.Code, error.ColumnName, error.Message);
    }

    private static SemanticTypeToolResult MapSemanticType(SemanticTypeDefinition definition)
    {
        return new SemanticTypeToolResult(
            definition.Name,
            definition.DisplayName,
            definition.Description,
            definition.BaseType,
            definition.ParentType,
            definition.Aliases?.ToArray() ?? Array.Empty<string>(),
            definition.Examples?.ToArray() ?? Array.Empty<string>(),
            definition.ValidationPattern,
            definition.ValidationPatterns?.ToArray() ?? Array.Empty<string>(),
            definition.NormalizationRules?.ToArray() ?? Array.Empty<string>());
    }

    private static RecordResult MapRecord(CollectionDefinition collectionDefinition, StorageRecord record)
    {
        var columnTypes = collectionDefinition.Columns.ToDictionary(
            column => column.Name,
            column => column.Type,
            StringComparer.Ordinal);
        var fields = record.Fields.ToDictionary(
            pair => pair.Key,
            pair => ColumnValueMapper.ToString(columnTypes[pair.Key], ColumnValueMapper.FromStorageValue(pair.Value)),
            StringComparer.Ordinal);
        return new RecordResult(record.Id.ToString(), record.CollectionName, fields);
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.Compiled)]
    private static partial Regex NameRegex();
}
