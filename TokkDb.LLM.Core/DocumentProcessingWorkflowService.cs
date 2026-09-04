using ExcelDataReader;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Text;
using TokkDb.LLM.Core.Orchestration;

namespace TokkDb.LLM.Core;

public sealed class DocumentProcessingWorkflowService : IDocumentProcessingWorkflowService
{
    private readonly IStorageToolGateway _storageTools;
    private readonly ISemanticTypeAgent _semanticTypeAgent;
    private readonly ILogger<DocumentProcessingWorkflowService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DocumentProcessingContext? _context;

    public DocumentProcessingWorkflowService(
        IStorageToolGateway storageTools,
        ISemanticTypeAgent semanticTypeAgent,
        ILogger<DocumentProcessingWorkflowService> logger)
    {
        _storageTools = storageTools;
        _semanticTypeAgent = semanticTypeAgent;
        _logger = logger;
    }

    public DocumentProcessingContext? GetCurrentContext() => _context;

    public async Task<DocumentProcessingContext> StartAsync(
        string filePath,
        ConversationRequest providerConfiguration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(providerConfiguration);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ValidateDocumentPath(filePath);
            var startedAt = DateTimeOffset.UtcNow;
            var context = new DocumentProcessingContext(
                Guid.NewGuid().ToString("N"),
                ProcessingState.Analyzing,
                providerConfiguration,
                filePath,
                Path.GetFileName(filePath),
                Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant(),
                Array.Empty<DocumentTablePlan>(),
                Array.Empty<SchemaChangeProposalRequest>(),
                0,
                null,
                null,
                0,
                Array.Empty<DocumentInvalidRecord>(),
                "Analyzing document structure.",
                startedAt,
                startedAt,
                [$"[{startedAt:O}] Analyzing: Started document analysis."]);
            _context = context;

            var tables = await AnalyzeDocumentAsync(filePath, providerConfiguration, cancellationToken);
            context = Transition(
                context,
                ProcessingState.ExtractingData,
                "Document structure analyzed.",
                tables: tables);

            var schemaChanges = await BuildSchemaChangesAsync(context, cancellationToken);
            context = Transition(
                context,
                ProcessingState.Analyzing,
                "Analyzing schema impact including display and validation dependencies.",
                schemaChanges: schemaChanges);
            context = Transition(
                context,
                schemaChanges.Count == 0 ? ProcessingState.ValidatingData : ProcessingState.WaitingForUser,
                schemaChanges.Count == 0
                    ? "No schema changes required."
                    : "Schema changes detected. Waiting for your confirmation.",
                schemaChanges: schemaChanges);

            if (schemaChanges.Count > 0)
            {
                var decisionRequest = BuildInitialDecisionRequest(context);
                context = Transition(
                    context,
                    ProcessingState.WaitingForUser,
                    "Schema changes require user decision.",
                    pendingDecisionRequest: decisionRequest);
                _context = context;
                return context;
            }

            context = await ExtractNormalizeValidateAndSaveAsync(context, cancellationToken);
            _context = context;
            return context;
        }
        catch (OperationCanceledException)
        {
            var cancelled = _context is null
                ? CreateCancelled(providerConfiguration, filePath)
                : Transition(_context, ProcessingState.Cancelled, "Document processing was cancelled.");
            _context = cancelled;
            return cancelled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Document processing start failed for file '{FilePath}'.", filePath);
            var failed = _context is null
                ? CreateFailure(providerConfiguration, filePath, ex.Message)
                : Transition(_context, ProcessingState.Failed, "Document processing failed.", failureReason: ex.Message);
            _context = failed;
            return failed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DocumentProcessingContext> ResumeAsync(
        string operationId,
        WorkflowDecision decision,
        string? additionalInstructions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_context is null)
            {
                throw new InvalidOperationException("No active document processing operation exists.");
            }

            if (!string.Equals(_context.OperationId, operationId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Operation '{operationId}' is not active.");
            }

            if (_context.State != ProcessingState.WaitingForUser || _context.PendingDecisionRequest is null)
            {
                throw new InvalidOperationException("Current document operation is not waiting for user input.");
            }

            var selectedAction = _context.PendingDecisionRequest.AvailableActions
                .FirstOrDefault(action => action.Decision == decision)
                ?? _context.PendingDecisionRequest.AvailableActions.FirstOrDefault();
            if (selectedAction is null)
            {
                throw new InvalidOperationException("No available action was found for the current decision request.");
            }

            if (decision == WorkflowDecision.ProvideInstructions && string.IsNullOrWhiteSpace(additionalInstructions))
            {
                var unchanged = Transition(
                    _context,
                    ProcessingState.WaitingForUser,
                    "Additional instructions are required.",
                    pendingDecisionRequest: _context.PendingDecisionRequest,
                    additionalInstructions: additionalInstructions);
                _context = unchanged;
                return unchanged;
            }

            if (decision == WorkflowDecision.Reject)
            {
                var cancelled = Transition(
                    _context,
                    ProcessingState.Cancelled,
                    "Document processing was cancelled by user.",
                    clearPendingDecisionRequest: true,
                    clearPendingConfirmationId: true,
                    additionalInstructions: additionalInstructions);
                _context = cancelled;
                return cancelled;
            }

            var context = Transition(
                _context,
                ProcessingState.Resuming,
                "Resuming document processing.",
                clearPendingDecisionRequest: true,
                additionalInstructions: additionalInstructions);
            context = await ApplySchemaChangesAsync(context, additionalInstructions, cancellationToken);
            if (context.State == ProcessingState.WaitingForUser)
            {
                _context = context;
                return context;
            }

            context = await ExtractNormalizeValidateAndSaveAsync(context, cancellationToken);
            _context = context;
            return context;
        }
        catch (OperationCanceledException)
        {
            if (_context is null)
            {
                throw;
            }

            var cancelled = Transition(_context, ProcessingState.Cancelled, "Document processing was cancelled.");
            _context = cancelled;
            return cancelled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Document processing resume failed for operation '{OperationId}'.", operationId);
            if (_context is null)
            {
                throw;
            }

            var failed = Transition(_context, ProcessingState.Failed, "Document processing failed.", failureReason: ex.Message);
            _context = failed;
            return failed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyCollection<DocumentTablePlan>> AnalyzeDocumentAsync(
        string filePath,
        ConversationRequest providerConfiguration,
        CancellationToken cancellationToken)
    {
        var tables = ReadDocumentTables(filePath);
        var semanticTypes = _storageTools.GetSemanticTypes();
        var availableSemanticTypes = semanticTypes.Success && semanticTypes.Data is not null
            ? semanticTypes.Data
            : Array.Empty<SemanticTypeToolResult>();
        var plans = new List<DocumentTablePlan>();

        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetCollectionName = InferTargetCollectionName(table.Name);
            var collectionSchema = _storageTools.GetCollectionSchema(targetCollectionName);
            var isNewCollection = !collectionSchema.Success || collectionSchema.Data is null;
            var existingColumns = isNewCollection
                ? new Dictionary<string, ColumnSchemaResult>(StringComparer.OrdinalIgnoreCase)
                : collectionSchema.Data!.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
            var columnPlans = new List<DocumentColumnPlan>(table.Columns.Count);

            foreach (var sourceColumnName in table.Columns)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetColumnName = InferTargetColumnName(sourceColumnName);
                var samples = table.Rows
                    .Select(row => row.Values.TryGetValue(sourceColumnName, out var value) ? value : null)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Take(20)
                    .Cast<string>()
                    .ToArray();

                var type = existingColumns.TryGetValue(targetColumnName, out var existing)
                    ? existing.Type
                    : InferColumnType(samples);
                var isNewColumn = isNewCollection || !existingColumns.ContainsKey(targetColumnName);

                var semanticType = existingColumns.TryGetValue(targetColumnName, out var existingColumn)
                    ? existingColumn.SemanticTypeName
                    : null;
                var semanticConfidence = 0d;
                var semanticReason = "No semantic resolution performed.";

                if (string.IsNullOrWhiteSpace(semanticType) && availableSemanticTypes.Count > 0)
                {
                    try
                    {
                        var resolution = await _semanticTypeAgent.ResolveAsync(
                            providerConfiguration,
                            new SemanticTypeResolutionInput(
                                targetColumnName,
                                null,
                                samples,
                                availableSemanticTypes,
                                type.ToString()),
                            cancellationToken);
                        semanticType = resolution.SuggestedSemanticTypeName;
                        semanticConfidence = resolution.Confidence;
                        semanticReason = resolution.Reason;
                        if (resolution.ProposedSemanticType is not null)
                        {
                            var registration = _storageTools.RegisterSemanticType(resolution.ProposedSemanticType);
                            if (registration.Success && registration.Data is not null)
                            {
                                semanticType = registration.Data.Name;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Semantic resolution failed for column '{ColumnName}'.", targetColumnName);
                    }
                }

                columnPlans.Add(new DocumentColumnPlan(
                    sourceColumnName,
                    targetColumnName,
                    isNewColumn,
                    type,
                    semanticType,
                    semanticConfidence,
                    semanticReason));
            }

            plans.Add(new DocumentTablePlan(
                table.Name,
                targetCollectionName,
                isNewCollection,
                columnPlans,
                table.Rows));
        }
        return plans;
    }

    /// <summary>
    /// Works out which schema changes the import needs. Nothing is created or
    /// applied here: each change is carried as a request and applied later
    /// through ChangeSchema, one at a time.
    /// </summary>
    private async Task<IReadOnlyCollection<SchemaChangeProposalRequest>> BuildSchemaChangesAsync(
        DocumentProcessingContext context,
        CancellationToken cancellationToken)
    {
        var schemaChanges = new List<SchemaChangeProposalRequest>();
        foreach (var table in context.Tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (table.IsNewCollection)
            {
                var columns = table.Columns
                    .Select(column => new ColumnToolDefinition(
                        column.TargetColumnName,
                        column.Type,
                        $"Imported from '{column.SourceColumnName}' in '{context.FileName}'.",
                        false,
                        false,
                        false,
                        null,
                        column.SemanticTypeName))
                    .ToList();
                var metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["sourceDocument"] = context.FileName,
                    ["sourceType"] = context.FileType,
                    ["sourceTable"] = table.SourceTableName,
                    ["detectedColumns"] = table.Columns.Count.ToString(),
                    ["detectedRows"] = table.Rows.Count.ToString(),
                    ["purpose"] = "Generated by document processing workflow for user and LLM analysis."
                };
                var request = new SchemaChangeProposalRequest(
                    SchemaChangeOperationType.CreateCollection,
                    table.TargetCollectionName,
                    null,
                    null,
                    $"Create collection '{table.TargetCollectionName}' from uploaded document.",
                    new SchemaChangeDefinition(
                        $"Imported from '{context.FileName}' table '{table.SourceTableName}'.",
                        metadata,
                        columns,
                        null,
                        null),
                    $"Detected new collection '{table.TargetCollectionName}' from document '{context.FileName}'.",
                    "DocumentAnalysisAgent",
                    "ReviewAndApproveSchemaChange");
                schemaChanges.Add(request);
            }
            else
            {
                foreach (var column in table.Columns.Where(column => column.IsNewColumn))
                {
                    var request = new SchemaChangeProposalRequest(
                        SchemaChangeOperationType.AddColumn,
                        table.TargetCollectionName,
                        column.TargetColumnName,
                        null,
                        $"Add nullable column '{column.TargetColumnName}' to '{table.TargetCollectionName}'.",
                        new SchemaChangeDefinition(
                            null,
                            null,
                            null,
                            new ColumnToolDefinition(
                                column.TargetColumnName,
                                column.Type,
                                $"Imported from '{column.SourceColumnName}' in '{context.FileName}'.",
                                false,
                                false,
                                false,
                                null,
                                column.SemanticTypeName),
                            null),
                        $"Detected a new column '{column.TargetColumnName}' while importing '{context.FileName}'.",
                        "DocumentAnalysisAgent",
                        "ReviewAndApproveSchemaChange");
                    schemaChanges.Add(request);
                }
            }
        }

        return schemaChanges;
    }

    /// <summary>
    /// Applies the import's schema changes one at a time through ChangeSchema.
    ///
    /// A change that ChangeSchema can apply outright advances the pipeline
    /// immediately. One that needs approval pauses the pipeline in its own
    /// WaitingForUser state, and the user's answer is relayed with
    /// ConfirmSchemaChange - the same two calls the chat agent uses.
    /// </summary>
    private async Task<DocumentProcessingContext> ApplySchemaChangesAsync(
        DocumentProcessingContext context,
        string? additionalInstructions,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(context.PendingConfirmationId))
            {
                if (context.PendingDecisionRequest is null)
                {
                    throw new InvalidOperationException("A schema change is awaiting confirmation but has no decision request.");
                }

                var actionId = SelectActionId(context.PendingDecisionRequest, additionalInstructions);
                var approved = !string.Equals(actionId, "reject", StringComparison.OrdinalIgnoreCase);

                var confirmed = _storageTools.ConfirmSchemaChange(
                    context.PendingConfirmationId,
                    approved,
                    additionalInstructions);

                if (!confirmed.Success || confirmed.Data is null)
                {
                    throw new InvalidOperationException(
                        $"Failed to confirm schema change '{context.PendingConfirmationId}': " +
                        string.Join("; ", confirmed.Errors.Select(error => error.Message)));
                }

                if (confirmed.Data.Outcome == SchemaChangeOutcome.Rejected)
                {
                    throw new InvalidOperationException(
                        $"The schema change to '{confirmed.Data.TargetCollection}' was declined, so the import cannot continue.");
                }

                context = Transition(
                    context,
                    ProcessingState.ApplyingChanges,
                    confirmed.Data.Summary,
                    pendingDecisionRequest: null,
                    clearPendingConfirmationId: true,
                    currentProposalIndex: context.CurrentProposalIndex + 1,
                    additionalInstructions: additionalInstructions);
                continue;
            }

            if (context.CurrentProposalIndex >= context.SchemaChanges.Count)
            {
                return Transition(
                    context,
                    ProcessingState.ValidatingData,
                    "Schema updates applied successfully.",
                    clearPendingDecisionRequest: true,
                    clearPendingConfirmationId: true,
                    additionalInstructions: additionalInstructions);
            }

            var change = context.SchemaChanges.ElementAt(context.CurrentProposalIndex);
            var result = _storageTools.ChangeSchema(change);
            if (!result.Success || result.Data is null)
            {
                throw new InvalidOperationException(
                    $"Failed to apply schema change to '{change.TargetCollection}': " +
                    string.Join("; ", result.Errors.Select(error => error.Message)));
            }

            if (result.Data.Outcome == SchemaChangeOutcome.AwaitingConfirmation &&
                result.Data.Confirmation is not null)
            {
                // The pipeline pauses in its own WaitingForUser state; the
                // decision is the one ChangeSchema built from the impact.
                return Transition(
                    context,
                    ProcessingState.WaitingForUser,
                    result.Data.Summary,
                    pendingDecisionRequest: WorkflowDecisionMapper.ToDecisionRequest(
                        context.OperationId,
                        result.Data.Confirmation,
                        "Schema change requires your decision"),
                    pendingConfirmationId: result.Data.ConfirmationId,
                    additionalInstructions: additionalInstructions);
            }

            context = Transition(
                context,
                ProcessingState.ApplyingChanges,
                result.Data.Summary,
                pendingDecisionRequest: null,
                clearPendingConfirmationId: true,
                currentProposalIndex: context.CurrentProposalIndex + 1,
                additionalInstructions: additionalInstructions);
        }
    }

    private async Task<DocumentProcessingContext> ExtractNormalizeValidateAndSaveAsync(
        DocumentProcessingContext context,
        CancellationToken cancellationToken)
    {
        var inValidation = Transition(context, ProcessingState.ValidatingData, "Validating extracted rows.");
        var inSaving = Transition(inValidation, ProcessingState.SavingData, "Saving valid records.");
        var savedCount = inSaving.SavedRecordCount;
        var invalidRecords = inSaving.InvalidRecords.ToList();

        foreach (var table in inSaving.Tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var row in table.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var column in table.Columns)
                {
                    row.Values.TryGetValue(column.SourceColumnName, out var value);
                    values[column.TargetColumnName] = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                }

                var result = _storageTools.InsertRecord(table.TargetCollectionName, values);
                if (!result.Success)
                {
                    invalidRecords.Add(new DocumentInvalidRecord(
                        table.TargetCollectionName,
                        row.RowNumber,
                        result.Errors.ToArray()));
                    continue;
                }

                savedCount++;
            }
        }

        await Task.CompletedTask;
        var summary = BuildSaveSummary(savedCount, invalidRecords.Count);
        return Transition(
            inSaving,
            ProcessingState.Completed,
            summary,
            savedRecordCount: savedCount,
            invalidRecords: invalidRecords,
            clearPendingDecisionRequest: true,
            clearPendingConfirmationId: true);
    }

    private static string BuildSaveSummary(int saved, int invalid)
    {
        var builder = new StringBuilder();
        builder.Append("Document processing completed. ");
        builder.Append($"Saved records: {saved}. ");
        builder.Append($"Invalid records: {invalid}.");
        return builder.ToString();
    }

    private static string SelectActionId(UserDecisionRequest request, string? additionalInstructions)
    {
        var desiredDecision = string.IsNullOrWhiteSpace(additionalInstructions)
            ? WorkflowDecision.Approve
            : WorkflowDecision.ProvideInstructions;

        var action = request.AvailableActions.FirstOrDefault(candidate => candidate.Decision == desiredDecision)
                     ?? request.AvailableActions.FirstOrDefault(candidate => candidate.Decision == WorkflowDecision.Approve)
                     ?? request.AvailableActions.First();
        return action.ActionId;
    }

    private static void ValidateDocumentPath(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Document file was not found.", filePath);
        }

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (extension is not ".csv" and not ".xlsx")
        {
            throw new InvalidOperationException("Only CSV and XLSX files are supported.");
        }
    }

    private static string InferTargetCollectionName(string sourceName)
    {
        return NormalizeIdentifier(sourceName, "Collection");
    }

    private static string InferTargetColumnName(string sourceName)
    {
        return NormalizeIdentifier(sourceName, "Column");
    }

    private static string NormalizeIdentifier(string input, string fallbackPrefix)
    {
        var raw = string.IsNullOrWhiteSpace(input)
            ? fallbackPrefix
            : input.Trim();
        var builder = new StringBuilder(raw.Length);
        var previousUnderscore = false;
        foreach (var character in raw)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousUnderscore = false;
            }
            else if (!previousUnderscore)
            {
                builder.Append('_');
                previousUnderscore = true;
            }
        }

        var normalized = builder.ToString().Trim('_');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = fallbackPrefix;
        }

        if (!char.IsLetter(normalized[0]))
        {
            normalized = $"{fallbackPrefix}_{normalized}";
        }

        return normalized;
    }

    private static ColumnType InferColumnType(IReadOnlyCollection<string> values)
    {
        if (values.Count == 0)
        {
            return ColumnType.String;
        }

        if (values.All(value => Guid.TryParse(value, out _)))
        {
            return ColumnType.Guid;
        }

        if (values.All(value => bool.TryParse(value, out _)))
        {
            return ColumnType.Boolean;
        }

        if (values.All(value => int.TryParse(value, out _)))
        {
            return ColumnType.Int32;
        }

        if (values.All(value => long.TryParse(value, out _)))
        {
            return ColumnType.Int64;
        }

        if (values.All(value => decimal.TryParse(value, out _)))
        {
            return ColumnType.Decimal;
        }

        if (values.All(value => DateTime.TryParse(value, out _)))
        {
            return ColumnType.DateTime;
        }

        return ColumnType.String;
    }

    private static IReadOnlyCollection<DocumentTableDataSource> ReadDocumentTables(string filePath)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = string.Equals(Path.GetExtension(filePath), ".csv", StringComparison.OrdinalIgnoreCase)
            ? ExcelReaderFactory.CreateCsvReader(stream)
            : ExcelReaderFactory.CreateReader(stream);

        var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration
        {
            UseColumnDataType = false,
            ConfigureDataTable = _ => new ExcelDataTableConfiguration
            {
                UseHeaderRow = true
            }
        });

        var tables = new List<DocumentTableDataSource>();
        foreach (DataTable table in dataSet.Tables)
        {
            var columns = table.Columns
                .Cast<DataColumn>()
                .Select(column => string.IsNullOrWhiteSpace(column.ColumnName)
                    ? $"Column{column.Ordinal + 1}"
                    : column.ColumnName.Trim())
                .ToArray();
            if (columns.Length == 0)
            {
                continue;
            }

            var rows = new List<DocumentRowData>();
            for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
            {
                var sourceRow = table.Rows[rowIndex];
                var values = new Dictionary<string, string?>(StringComparer.Ordinal);
                var hasValues = false;
                for (var columnIndex = 0; columnIndex < columns.Length; columnIndex++)
                {
                    var value = sourceRow[columnIndex]?.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        hasValues = true;
                    }

                    values[columns[columnIndex]] = string.IsNullOrWhiteSpace(value) ? null : value;
                }

                if (!hasValues)
                {
                    continue;
                }

                rows.Add(new DocumentRowData(rowIndex + 2, values));
            }

            tables.Add(new DocumentTableDataSource(
                string.IsNullOrWhiteSpace(table.TableName) ? "Sheet1" : table.TableName.Trim(),
                columns,
                rows));
        }

        if (tables.Count == 0)
        {
            throw new InvalidOperationException("No tabular data was detected in the uploaded document.");
        }

        return tables;
    }

    private UserDecisionRequest BuildInitialDecisionRequest(DocumentProcessingContext context)
    {
        // Impact is no longer computed up front: each change is analysed by
        // ChangeSchema when its turn comes, and carries its own confirmation.
        // This first decision is about the import as a whole.
        var changes = context.SchemaChanges;
        var records = 0;
        var queries = 0;
        var rules = 0;
        var relations = 0;
        var summary = string.Join(
            Environment.NewLine,
            changes.Select(change =>
                $"- {change.OperationType}: {change.TargetCollection}{(string.IsNullOrWhiteSpace(change.TargetColumn) ? string.Empty : $".{change.TargetColumn}")}"));

        return new UserDecisionRequest
        {
            OperationId = context.OperationId,
            Title = "Document import requires your decision",
            Message =
                $"Operation:{Environment.NewLine}" +
                $"Apply schema changes before import.{Environment.NewLine}{Environment.NewLine}" +
                $"Detected impacts:{Environment.NewLine}" +
                $"- Records potentially affected: {records}{Environment.NewLine}" +
                $"- Saved queries affected: {queries}{Environment.NewLine}" +
                $"- Display/validation rules affected: {rules}{Environment.NewLine}" +
                $"- Relations affected: {relations}{Environment.NewLine}{Environment.NewLine}" +
                $"Proposed schema changes:{Environment.NewLine}{summary}",
            AvailableActions =
            [
                new WorkflowAction
                {
                    ActionId = "approve",
                    Title = "Approve",
                    Description = "Apply proposed schema changes and continue importing data.",
                    Decision = WorkflowDecision.Approve
                },
                new WorkflowAction
                {
                    ActionId = "reject",
                    Title = "Reject",
                    Description = "Cancel the document import operation.",
                    Decision = WorkflowDecision.Reject
                },
                new WorkflowAction
                {
                    ActionId = "provide_instructions",
                    Title = "Provide Instructions",
                    Description = "Provide custom instructions for schema and data processing.",
                    Decision = WorkflowDecision.ProvideInstructions
                }
            ]
        };
    }

    private static DocumentProcessingContext Transition(
        DocumentProcessingContext context,
        ProcessingState state,
        string statusMessage,
        IReadOnlyCollection<DocumentTablePlan>? tables = null,
        IReadOnlyCollection<SchemaChangeProposalRequest>? schemaChanges = null,
        int? currentProposalIndex = null,
        string? pendingConfirmationId = null,
        UserDecisionRequest? pendingDecisionRequest = null,
        bool clearPendingConfirmationId = false,
        bool clearPendingDecisionRequest = false,
        int? savedRecordCount = null,
        IReadOnlyCollection<DocumentInvalidRecord>? invalidRecords = null,
        string? additionalInstructions = null,
        string? failureReason = null)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var timeline = context.Timeline.ToList();
        timeline.Add($"[{timestamp:O}] {state}: {statusMessage}");

        return context with
        {
            State = state,
            StatusMessage = statusMessage,
            Tables = tables ?? context.Tables,
            SchemaChanges = schemaChanges ?? context.SchemaChanges,
            CurrentProposalIndex = currentProposalIndex ?? context.CurrentProposalIndex,
            PendingConfirmationId = clearPendingConfirmationId
                ? null
                : pendingConfirmationId ?? context.PendingConfirmationId,
            PendingDecisionRequest = clearPendingDecisionRequest
                ? null
                : pendingDecisionRequest ?? context.PendingDecisionRequest,
            SavedRecordCount = savedRecordCount ?? context.SavedRecordCount,
            InvalidRecords = invalidRecords ?? context.InvalidRecords,
            UpdatedUtc = timestamp,
            Timeline = timeline,
            AdditionalInstructions = additionalInstructions ?? context.AdditionalInstructions,
            FailureReason = failureReason
        };
    }

    private static DocumentProcessingContext CreateCancelled(ConversationRequest providerConfiguration, string filePath)
    {
        var now = DateTimeOffset.UtcNow;
        return new DocumentProcessingContext(
            Guid.NewGuid().ToString("N"),
            ProcessingState.Cancelled,
            providerConfiguration,
            filePath,
            Path.GetFileName(filePath),
            Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant(),
            Array.Empty<DocumentTablePlan>(),
            Array.Empty<SchemaChangeProposalRequest>(),
            0,
            null,
            null,
            0,
            Array.Empty<DocumentInvalidRecord>(),
            "Document processing cancelled.",
            now,
            now,
            [$"[{now:O}] Cancelled: Document processing cancelled."]);
    }

    private static DocumentProcessingContext CreateFailure(
        ConversationRequest providerConfiguration,
        string filePath,
        string reason)
    {
        var now = DateTimeOffset.UtcNow;
        return new DocumentProcessingContext(
            Guid.NewGuid().ToString("N"),
            ProcessingState.Failed,
            providerConfiguration,
            filePath,
            Path.GetFileName(filePath),
            Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant(),
            Array.Empty<DocumentTablePlan>(),
            Array.Empty<SchemaChangeProposalRequest>(),
            0,
            null,
            null,
            0,
            Array.Empty<DocumentInvalidRecord>(),
            "Document processing failed.",
            now,
            now,
            [$"[{now:O}] Failed: {reason}"],
            null,
            reason);
    }

    private sealed record DocumentTableDataSource(
        string Name,
        IReadOnlyCollection<string> Columns,
        IReadOnlyCollection<DocumentRowData> Rows);
}
