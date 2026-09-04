using System.ComponentModel;

namespace TokkDb.LLM.Core;

public interface IStorageToolGateway
{
    /// <summary>
    /// Validates a schema change, analyses its impact, and either applies it or
    /// returns a confirmation request. One call covers the whole flow, and this
    /// is the only schema-change entry point exposed to agents.
    /// </summary>
    StorageToolResult<SchemaChangeOperationResult> ChangeSchema(SchemaChangeProposalRequest request);


    /// <summary>
    /// Applies or discards a change that was held for confirmation.
    /// </summary>
    StorageToolResult<SchemaChangeOperationResult> ConfirmSchemaChange(
        string confirmationId,
        bool approved,
        string? note);

    StorageToolResult<IReadOnlyCollection<SemanticTypeToolResult>> GetSemanticTypes();

    StorageToolResult<SemanticTypeToolResult> RegisterSemanticType(SemanticTypeToolDefinition semanticType);

    StorageToolResult<IReadOnlyCollection<string>> GetCollections();

    StorageToolResult<CollectionSchemaResult> GetCollectionSchema(string collectionName);

    StorageToolResult<DisplayRuleToolResult> GetDisplayRule(string collectionName);

    StorageToolResult<DisplayRuleToolResult> ValidateDisplayRule(string collectionName, string template);

    StorageToolResult<DisplayRuleProposalResult> ProposeDisplayRule(DisplayRuleProposalRequest request);

    /// <summary>
    /// Presentation command: resolves records for display in the chat. Performs
    /// no query of its own - the agent supplies ids obtained from a query tool.
    /// </summary>
    StorageToolResult<RecordsDisplayMessage> ShowRecords(ShowRecordsRequest request);

    /// <summary>
    /// The one way to read records. Validates a declarative query against the
    /// collection schema and, when it is valid, runs it immediately and returns
    /// the rows. There is no stored plan: validation and execution happen in the
    /// one call.
    ///
    /// It also covers what used to be separate lookups - a single record by id,
    /// a whole collection, a single field equalling a value - so that there is
    /// only one tool an agent has to choose correctly.
    /// </summary>
    StorageToolResult<RecordQueryResult> QueryRecords(RecordQuery query);

    /// <summary>
    /// Aggregate analysis over a collection - counting by value, duplicates,
    /// unreferenced records. Validated and executed in the one call, like
    /// <see cref="QueryRecords"/>: there is no plan to store and no id to carry
    /// between calls.
    ///
    /// Anything expressible as a filter, a sort and a limit belongs in
    /// <see cref="QueryRecords"/> instead; this covers only what a row-returning
    /// query cannot express.
    /// </summary>
    StorageToolResult<DataQueryExecutionResult> AnalyzeRecords(DataQueryDefinition definition);

    StorageToolResult<RecordResult> InsertRecord(string collectionName, Dictionary<string, string?> fields);

    StorageToolResult<RecordResult> UpdateRecord(string collectionName, string recordId, Dictionary<string, string?> fields);

    StorageToolResult<DeleteRecordResult> DeleteRecord(string collectionName, string recordId);
}

public sealed record StorageToolError(string Code, string? Field, string Message);

public interface IStorageToolResult
{
    bool Success { get; }

    IReadOnlyCollection<StorageToolError> Errors { get; }
}

public sealed record StorageToolResult<T>(bool Success, T? Data, IReadOnlyCollection<StorageToolError> Errors) : IStorageToolResult
{
    public static StorageToolResult<T> Ok(T data) => new(true, data, Array.Empty<StorageToolError>());

    public static StorageToolResult<T> Fail(params StorageToolError[] errors) => new(false, default, errors);

    public static StorageToolResult<T> Fail(IReadOnlyCollection<StorageToolError> errors) => new(false, default, errors);
}

public sealed record CollectionSchemaResult(
    string Name,
    string? Description,
    int SchemaVersion,
    IReadOnlyCollection<ColumnSchemaResult> Columns,
    IReadOnlyCollection<RelationSchemaResult> Relations);

public sealed record SchemaChangeResult(
    string CollectionName,
    int SchemaVersion,
    string Change);

public sealed record RelationChangeResult(
    string RelationName,
    string SourceCollection,
    int SourceSchemaVersion,
    string TargetCollection,
    int TargetSchemaVersion,
    string Change);

/// <summary>Definition of one column.</summary>
public sealed record ColumnToolDefinition(
    [property: Description("Column name. Letters, digits and underscores, starting with a letter.")]
    string Name,

    [property: Description("Value type: String, Boolean, Int32, Int64, Decimal, DateTime or Guid.")]
    ColumnType Type,

    [property: Description("What the column holds, in one short sentence. Helps later reasoning about the data.")]
    string? Description = null,

    [property: Description("True when this column identifies the record uniquely.")]
    bool PrimaryKey = false,

    [property: Description("True when no two records may share a value.")]
    bool Unique = false,

    [property: Description("True when the value may not be changed after the record is created.")]
    bool ReadOnly = false,

    [property: Description("Value used when a record omits this column, written as text.")]
    string? DefaultValue = null,

    [property: Description("Name of a registered semantic type, such as an email or phone type. Call ResolveSemanticType to find the one that fits a column.")]
    string? SemanticTypeName = null,

    [property: Description("Optional regular expression every value must match.")]
    string? ValidationPattern = null,

    [property: Description("Optional additional regular expressions every value must match.")]
    List<string>? ValidationPatterns = null);

/// <summary>
/// A semantic type: what a value means, beyond its raw storage type.
/// </summary>
public sealed record SemanticTypeToolDefinition(
    [property: Description("Unique identifier, lowercase and without spaces, such as 'email' or 'phone'.")]
    string Name,

    [property: Description("Human-readable name, such as 'Email address'.")]
    string DisplayName,

    [property: Description("What this type represents and when to use it, in one or two sentences.")]
    string Description,

    [property: Description("Underlying value type: String, Boolean, Int32, Int64, Decimal, DateTime or Guid.")]
    ColumnType BaseType,

    [property: Description("Name of a broader semantic type this one specialises, if any.")]
    string? ParentType = null,

    [property: Description("Other names a column might use for this concept, used when matching columns to types.")]
    List<string>? Aliases = null,

    [property: Description("A few representative values.")]
    List<string>? Examples = null,

    [property: Description("Regular expression every value must match.")]
    string? ValidationPattern = null,

    [property: Description("Additional regular expressions every value must match.")]
    List<string>? ValidationPatterns = null,

    [property: Description(
        "Normalisation applied before a value is stored or compared. Supported rules: Trim, ToLowerInvariant, " +
        "ToUpperInvariant. Values are stored normalised, so searches are normalised the same way.")]
    List<string>? NormalizationRules = null);

public sealed record SemanticTypeToolResult(
    string Name,
    string DisplayName,
    string Description,
    ColumnType BaseType,
    string? ParentType,
    IReadOnlyCollection<string> Aliases,
    IReadOnlyCollection<string> Examples,
    string? ValidationPattern,
    IReadOnlyCollection<string> ValidationPatterns,
    IReadOnlyCollection<string> NormalizationRules);

public sealed record ColumnSchemaResult(
    string Name,
    ColumnType Type,
    string? Description,
    bool Unique,
    bool ReadOnly,
    string? DefaultValue,
    string? SemanticTypeName,
    string? ValidationPattern,
    IReadOnlyCollection<string> ValidationPatterns);

public sealed record RelationSchemaResult(
    string Name,
    string Type,
    string SourceCollection,
    string SourceColumn,
    string TargetCollection,
    string TargetColumn,
    string? Description);

/// <summary>
/// The kinds of analysis a row-returning query cannot express. Filtering,
/// sorting and limiting are not among them: those are QueryRecords.
/// </summary>
public enum DataQueryType
{
    MostFrequent,
    FindDuplicates,
    FindUnreferenced
}

/// <summary>
/// Definition of an aggregate-style data query. For ordinary filtering,
/// sorting and paging use QueryRecords instead.
/// </summary>
public sealed record DataQueryDefinition(
    [property: Description(
        "Kind of analysis. MostFrequent counts records per distinct value and returns the commonest; " +
        "FindDuplicates returns values shared by more than one record; " +
        "FindUnreferenced returns records whose key appears in no record of another collection.")]
    DataQueryType QueryType,

    [property: Description("Collection to analyse.")]
    string CollectionName,

    [property: Description("Columns to return, for FindUnreferenced. Omit for all columns.")]
    List<string>? SelectColumns = null,

    [property: Description("Columns whose combined value forms the group, for MostFrequent and FindDuplicates.")]
    List<string>? GroupByColumns = null,

    [property: Description("Maximum number of rows, between 1 and 500.")]
    int Limit = 50,

    [property: Description("Other collection to compare against, for FindUnreferenced.")]
    string? RelatedCollectionName = null,

    [property: Description("Key column in this collection used for the comparison, for FindUnreferenced.")]
    string? CollectionKeyColumn = null,

    [property: Description("Matching key column in the related collection, for FindUnreferenced.")]
    string? RelatedKeyColumn = null);

public sealed record DataQueryRow(
    IReadOnlyDictionary<string, string?> Fields);

public sealed record DataQueryExecutionResult(
    string Summary,
    int TotalRows,
    IReadOnlyCollection<DataQueryRow> Rows,
    IReadOnlyCollection<string> SemanticHints);

public sealed record RecordResult(
    string Id,
    string CollectionName,
    IReadOnlyDictionary<string, string?> Fields);

public sealed record DeleteRecordResult(
    string CollectionName,
    string RecordId,
    bool Deleted);

/// <summary>
/// Display rule of a collection as exposed to agents. Carries the template and
/// its validation state, never the CollectionDefinition itself.
/// </summary>
public sealed record DisplayRuleToolResult(
    string CollectionName,
    string? Template,
    bool IsValid,
    IReadOnlyCollection<string> ReferencedColumns,
    IReadOnlyCollection<string> MissingColumns,
    string? SamplePreview = null);

/// <summary>
/// Agent-authored display rule proposal. The agent proposes; the domain
/// validates and decides whether it can be applied.
/// </summary>
public sealed record DisplayRuleProposalRequest(
    [property: Description("Collection the display rule belongs to.")]
    string CollectionName,

    [property: Description(
        "Template for a record's display value. Column names go in braces and everything else is literal text, " +
        "for example '{FullName} - {Email}'. Only columns of this collection may be referenced.")]
    string Template,

    [property: Description("Why this template was chosen, in one short sentence.")]
    string? Reason = null);

public sealed record DisplayRuleProposalResult(
    string CollectionName,
    string Template,
    bool IsValid,
    bool Applied,
    bool RequiresUserDecision,
    IReadOnlyCollection<string> Errors,
    string? SamplePreview = null,
    string? PreviousTemplate = null);

public enum SchemaChangeOutcome
{
    /// <summary>The change was validated, analysed and applied.</summary>
    Applied,

    /// <summary>The change is analysed and held, waiting for the user to confirm.</summary>
    AwaitingConfirmation,

    /// <summary>The user declined the change; nothing was applied.</summary>
    Rejected
}

/// <summary>
/// Outcome of a schema change request.
///
/// A single call validates, analyses impact and either applies the change or
/// asks the user. When confirmation is needed the turn is suspended by the
/// orchestrator, not by this result.
/// </summary>
public sealed record SchemaChangeOperationResult(
    SchemaChangeOutcome Outcome,
    SchemaChangeOperationType OperationType,
    string TargetCollection,
    string? TargetColumn,
    SchemaImpactAnalysis Impact,
    string Summary,
    string? ConfirmationId = null)
{
    /// <summary>
    /// Decision to put to the user. Set only when
    /// <see cref="Outcome"/> is <see cref="SchemaChangeOutcome.AwaitingConfirmation"/>;
    /// the agent raises it through the ordinary user-interaction channel so the
    /// orchestrator suspends the turn.
    /// </summary>
    public UserInteractionRequest? Confirmation { get; init; }
}

public sealed class SchemaToolOptions
{
    public bool AllowSchemaChanges { get; init; }
}


public enum SchemaChangeOperationType
{
    CreateCollection,
    RemoveCollection,
    RenameCollection,
    AddColumn,
    RemoveColumn,
    RenameColumn,
    ChangeColumnType,
    ChangeColumnDescription,
    ChangeSemanticType,
    AddRelation,
    RemoveRelation
}

public sealed record SchemaImpactResult(
    int EstimatedCount,
    string Summary,
    IReadOnlyCollection<string> Items);

public enum SchemaImpactClassification
{
    Safe,
    RequiresConfirmation,
    RequiresUserInstruction,
    Invalid
}

public sealed record SchemaImpactAnalysis(
    SchemaImpactClassification Classification,
    string Summary,
    SchemaImpactResult ExistingRecords,
    SchemaImpactResult Columns,
    SchemaImpactResult Relations,
    SchemaImpactResult SemanticTypes,
    SchemaImpactResult ValidationRules,
    SchemaImpactResult DisplayRules,
    SchemaImpactResult SavedQueries,
    SchemaImpactResult PendingOperations,
    SchemaImpactResult DependentTools,
    IReadOnlyCollection<string> Notes,
    DateTimeOffset AnalyzedUtc);

/// <summary>
/// A proposed schema change. Nothing is applied by creating one.
/// </summary>
public sealed record SchemaChangeProposalRequest(
    [property: Description(
        "What the change does: CreateCollection, RemoveCollection, RenameCollection, AddColumn, RemoveColumn, " +
        "RenameColumn, ChangeColumnType, ChangeColumnDescription, ChangeSemanticType, AddRelation or RemoveRelation.")]
    SchemaChangeOperationType OperationType,

    [property: Description("Name of the collection the change applies to. Must already exist unless creating one.")]
    string TargetCollection,

    [property: Description(
        "Name of the column the change applies to. Required for every column operation, including AddColumn, " +
        "where it is the name of the column being added. Leave empty for collection and relation operations.")]
    string? TargetColumn,

    [property: Description(
        "Optional human-readable summary of the current state, as plain text. Not a structured value.")]
    string? CurrentDefinition,

    [property: Description(
        "Optional human-readable summary of the intended state, as plain text. Not a structured value: " +
        "put the actual change in 'definition'.")]
    string? ProposedDefinition,

    [property: Description(
        "The change itself. Use 'column' for a single-column operation, 'columns' when creating a collection, " +
        "and 'newName' when renaming.")]
    SchemaChangeDefinition? Definition,

    [property: Description("Why the change is being proposed, in one short sentence.")]
    string Reason,

    [property: Description("Who proposed it. Leave as 'AI' unless relaying a user's explicit request.")]
    string Source = "AI",

    [property: Description("Optional note about a decision the user must make before this is applied.")]
    string? RequiredUserAction = null);

public sealed record SchemaChangeProposal(
    string ProposalId,
    SchemaChangeOperationType OperationType,
    string TargetCollection,
    string? TargetColumn,
    string? CurrentDefinition,
    string? ProposedDefinition,
    SchemaChangeDefinition? Definition,
    string Reason,
    string Source,
    SchemaImpactResult AffectedData,
    SchemaImpactResult AffectedQueries,
    IReadOnlyCollection<string> AffectedRelations,
    IReadOnlyCollection<string> AffectedRules,
    string RequiredUserAction,
    SchemaImpactAnalysis? ImpactAnalysis,
    DateTimeOffset CreatedUtc);

/// <summary>
/// The concrete content of a schema change. Only the fields relevant to the
/// operation need to be set.
/// </summary>
public sealed record SchemaChangeDefinition(
    [property: Description("Description for the collection, when creating one or changing its description.")]
    string? CollectionDescription,

    [property: Description("Optional free-form metadata for the collection.")]
    Dictionary<string, string?>? CollectionMetadata,

    [property: Description("All columns of a new collection. Use when creating a collection.")]
    List<ColumnToolDefinition>? Columns,

    [property: Description("The single column being added or changed. Use for any column operation.")]
    ColumnToolDefinition? Column,

    [property: Description("The new name, when renaming a collection or a column.")]
    string? NewName);


