namespace TokkDb.LLM.Core;

public sealed record DocumentProcessingContext(
    string OperationId,
    ProcessingState State,
    ConversationRequest ProviderConfiguration,
    string FilePath,
    string FileName,
    string FileType,
    IReadOnlyCollection<DocumentTablePlan> Tables,
    /// <summary>
    /// Schema changes the import needs, held as requests rather than
    /// pre-created proposals: each is applied through ChangeSchema when its turn
    /// comes, so nothing is stored server-side before the user has agreed.
    /// </summary>
    IReadOnlyCollection<SchemaChangeProposalRequest> SchemaChanges,
    int CurrentProposalIndex,
    /// <summary>Confirmation the pipeline is waiting on, from ChangeSchema.</summary>
    string? PendingConfirmationId,
    UserDecisionRequest? PendingDecisionRequest,
    int SavedRecordCount,
    IReadOnlyCollection<DocumentInvalidRecord> InvalidRecords,
    string StatusMessage,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    IReadOnlyCollection<string> Timeline,
    string? AdditionalInstructions = null,
    string? FailureReason = null);

public sealed record DocumentTablePlan(
    string SourceTableName,
    string TargetCollectionName,
    bool IsNewCollection,
    IReadOnlyCollection<DocumentColumnPlan> Columns,
    IReadOnlyCollection<DocumentRowData> Rows);

public sealed record DocumentColumnPlan(
    string SourceColumnName,
    string TargetColumnName,
    bool IsNewColumn,
    ColumnType Type,
    string? SemanticTypeName,
    double SemanticConfidence,
    string SemanticReason);

public sealed record DocumentRowData(
    int RowNumber,
    IReadOnlyDictionary<string, string?> Values);

public sealed record DocumentInvalidRecord(
    string CollectionName,
    int RowNumber,
    IReadOnlyCollection<StorageToolError> Errors);
