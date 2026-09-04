using Microsoft.Extensions.Logging;
using TokkDb.LLM.Core;
using TokkDb.LLM.Storage;

namespace TokkDb.LLM.Application;

/// <summary>
/// Schema changes as a single operation.
///
/// One call validates the change, analyses its impact and then either applies it
/// or asks the user. There is no proposal to fetch, no workflow to start and no
/// separate resume step: the model cannot lose its place in a sequence that has
/// only one step.
///
/// Pausing for the user is deliberately not done here. A tool call cannot
/// suspend a turn; it can only return. So a change that needs confirmation is
/// held, and the decision travels back through the ordinary user-interaction
/// channel, which the orchestrator already knows how to suspend and resume.
/// </summary>
public sealed partial class StorageToolGateway
{
    public StorageToolResult<SchemaChangeOperationResult> ChangeSchema(SchemaChangeProposalRequest request)
    {
        if (request is null)
        {
            return Fail("InvalidSchemaChange", "request", "Schema change request is required.");
        }

        var notAllowed = EnsureSchemaChangesAllowed(nameof(ChangeSchema));
        if (notAllowed is not null)
        {
            return StorageToolResult<SchemaChangeOperationResult>.Fail(notAllowed);
        }

        if (!SupportsSchemaWorkflow(request.OperationType))
        {
            return Fail(
                "UnsupportedSchemaChange",
                "operationType",
                $"Operation '{request.OperationType}' cannot be applied through ChangeSchema.");
        }

        // Reuses the existing proposal builder for name validation, impact
        // analysis and construction. The proposal is still the internal shape;
        // it is simply no longer a step the model has to drive.
        var created = CreateSchemaChangeProposal(request);
        if (!created.Success || created.Data is null)
        {
            return StorageToolResult<SchemaChangeOperationResult>.Fail(created.Errors.ToArray());
        }

        var proposal = created.Data;
        var impact = proposal.ImpactAnalysis ?? BuildImpactAnalysis(proposal);

        var validationErrors = ValidateSchemaWorkflowProposal(proposal).ToArray();
        if (validationErrors.Length > 0)
        {
            _logger.LogWarning(
                "Schema change rejected by validation. Operation: {OperationType}, Collection: {CollectionName}, Column: {ColumnName}, Errors: {ValidationErrors}",
                proposal.OperationType,
                proposal.TargetCollection,
                proposal.TargetColumn,
                string.Join(" | ", validationErrors.Select(error => error.Message)));

            return StorageToolResult<SchemaChangeOperationResult>.Fail(validationErrors);
        }

        var decision = BuildWorkflowDecisionRequest(impact, proposal.ProposalId, proposal);
        if (decision is not null)
        {
            // Held, not applied. The agent raises the decision and the
            // orchestrator suspends the turn until the user answers.
            _schemaProposalStore.Save(proposal);

            _logger.LogInformation(
                "Schema change awaiting confirmation. ConfirmationId: {ConfirmationId}, Operation: {OperationType}, Collection: {CollectionName}, Column: {ColumnName}",
                proposal.ProposalId,
                proposal.OperationType,
                proposal.TargetCollection,
                proposal.TargetColumn);

            return StorageToolResult<SchemaChangeOperationResult>.Ok(new SchemaChangeOperationResult(
                SchemaChangeOutcome.AwaitingConfirmation,
                proposal.OperationType,
                proposal.TargetCollection,
                proposal.TargetColumn,
                impact,
                decision.Message,
                proposal.ProposalId)
            {
                Confirmation = new UserInteractionRequest(
                    decision.RequestId,
                    decision.Message,
                    decision.AvailableActions
                        .Select(action => new UserAction(action.ActionId, action.Title, action.Description))
                        .ToArray(),
                    DateTimeOffset.UtcNow)
            });
        }

        return Apply(proposal, impact);
    }

    public StorageToolResult<SchemaChangeOperationResult> ConfirmSchemaChange(
        string confirmationId,
        bool approved,
        string? note)
    {
        if (string.IsNullOrWhiteSpace(confirmationId))
        {
            return Fail("InvalidConfirmationId", "confirmationId", "Confirmation ID is required.");
        }

        var proposal = _schemaProposalStore.GetById(confirmationId.Trim());
        if (proposal is null)
        {
            return Fail(
                "ConfirmationNotFound",
                "confirmationId",
                $"No schema change is waiting under confirmation '{confirmationId}'.");
        }

        var impact = proposal.ImpactAnalysis ?? BuildImpactAnalysis(proposal);

        if (!approved)
        {
            _logger.LogInformation(
                "Schema change declined by the user. ConfirmationId: {ConfirmationId}, Operation: {OperationType}, Collection: {CollectionName}, Note: {Note}",
                confirmationId,
                proposal.OperationType,
                proposal.TargetCollection,
                note);

            return StorageToolResult<SchemaChangeOperationResult>.Ok(new SchemaChangeOperationResult(
                SchemaChangeOutcome.Rejected,
                proposal.OperationType,
                proposal.TargetCollection,
                proposal.TargetColumn,
                impact,
                "The change was declined and nothing was applied."));
        }

        var notAllowed = EnsureSchemaChangesAllowed(nameof(ConfirmSchemaChange));
        if (notAllowed is not null)
        {
            return StorageToolResult<SchemaChangeOperationResult>.Fail(notAllowed);
        }

        return Apply(proposal, impact);
    }

    // =====================================================================

    private StorageToolResult<SchemaChangeOperationResult> Apply(
        SchemaChangeProposal proposal,
        SchemaImpactAnalysis impact)
    {
        try
        {
            var summary = ApplySchemaWorkflowChange(proposal);

            _logger.LogInformation(
                "Schema change applied. Operation: {OperationType}, Collection: {CollectionName}, Column: {ColumnName}",
                proposal.OperationType,
                proposal.TargetCollection,
                proposal.TargetColumn);

            return StorageToolResult<SchemaChangeOperationResult>.Ok(new SchemaChangeOperationResult(
                SchemaChangeOutcome.Applied,
                proposal.OperationType,
                proposal.TargetCollection,
                proposal.TargetColumn,
                impact,
                summary));
        }
        catch (StorageValidationException ex)
        {
            _logger.LogWarning(
                ex,
                "Schema change failed domain validation. Operation: {OperationType}, Collection: {CollectionName}",
                proposal.OperationType,
                proposal.TargetCollection);

            return StorageToolResult<SchemaChangeOperationResult>.Fail(
                ex.Errors.Select(MapStorageError).ToArray());
        }
        catch (Exception ex)
        {
            // Internal detail stays in the log, never in the chat.
            _logger.LogError(
                ex,
                "Schema change failed. Operation: {OperationType}, Collection: {CollectionName}",
                proposal.OperationType,
                proposal.TargetCollection);

            return Fail("SchemaChangeFailed", null, "The schema change could not be applied.");
        }
    }

    private static StorageToolResult<SchemaChangeOperationResult> Fail(
        string code,
        string? field,
        string message) =>
        StorageToolResult<SchemaChangeOperationResult>.Fail(new StorageToolError(code, field, message));
}
