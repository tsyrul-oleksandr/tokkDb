using Microsoft.Extensions.Logging;
using TokkDb.LLM.Core;
using TokkDb.LLM.Storage;

namespace TokkDb.LLM.Application;

/// <summary>
/// Display rule tools exposed to AI agents.
///
/// Agents reach display rules only through here: they never touch
/// <see cref="CollectionDefinition"/> directly. Every proposal is validated
/// deterministically by the domain before it can be applied, and no LLM is
/// involved in validation or evaluation.
/// </summary>
public sealed partial class StorageToolGateway
{
    public StorageToolResult<DisplayRuleToolResult> GetDisplayRule(string collectionName)
    {
        var invalidName = ValidateName(collectionName, "collectionName", "collection");
        if (invalidName is not null)
        {
            return StorageToolResult<DisplayRuleToolResult>.Fail(invalidName);
        }

        var collection = _storageRuntime.Storage.GetCollectionDefinition(collectionName);
        if (collection is null)
        {
            return StorageToolResult<DisplayRuleToolResult>.Fail(CollectionNotFound(collectionName));
        }

        var rule = collection.DisplayRule;
        if (rule is null)
        {
            _logger.LogDebug(
                "DisplayRule requested but none configured. Collection: {CollectionName}",
                collection.Name);

            return StorageToolResult<DisplayRuleToolResult>.Ok(new DisplayRuleToolResult(
                collection.Name,
                null,
                false,
                Array.Empty<string>(),
                Array.Empty<string>()));
        }

        var validation = _displayRuleValidator.Validate(rule, collection);
        return StorageToolResult<DisplayRuleToolResult>.Ok(new DisplayRuleToolResult(
            collection.Name,
            rule.Template,
            validation.IsValid,
            validation.ReferencedColumns,
            validation.MissingColumns,
            BuildPreview(collection, rule)));
    }

    public StorageToolResult<DisplayRuleToolResult> ValidateDisplayRule(string collectionName, string template)
    {
        var invalidName = ValidateName(collectionName, "collectionName", "collection");
        if (invalidName is not null)
        {
            return StorageToolResult<DisplayRuleToolResult>.Fail(invalidName);
        }

        var collection = _storageRuntime.Storage.GetCollectionDefinition(collectionName);
        if (collection is null)
        {
            return StorageToolResult<DisplayRuleToolResult>.Fail(CollectionNotFound(collectionName));
        }

        var rule = DisplayRule.TryCreate(template);
        if (rule is null)
        {
            return StorageToolResult<DisplayRuleToolResult>.Fail(new StorageToolError(
                "InvalidDisplayRule",
                "template",
                $"Display rule template is required and must be at most {DisplayRule.MaxTemplateLength} characters."));
        }

        var validation = _displayRuleValidator.Validate(rule, collection);
        return StorageToolResult<DisplayRuleToolResult>.Ok(new DisplayRuleToolResult(
            collection.Name,
            rule.Template,
            validation.IsValid,
            validation.ReferencedColumns,
            validation.MissingColumns,
            validation.IsValid ? BuildPreview(collection, rule) : null));
    }

    /// <summary>
    /// Accepts an agent's proposal. A valid proposal is applied automatically
    /// when policy allows it; otherwise it is returned for a user decision.
    /// An invalid proposal is never applied.
    /// </summary>
    public StorageToolResult<DisplayRuleProposalResult> ProposeDisplayRule(DisplayRuleProposalRequest request)
    {
        if (request is null)
        {
            return StorageToolResult<DisplayRuleProposalResult>.Fail(new StorageToolError(
                "InvalidDisplayRuleProposal", "request", "Display rule proposal is required."));
        }

        var invalidName = ValidateName(request.CollectionName, "collectionName", "collection");
        if (invalidName is not null)
        {
            return StorageToolResult<DisplayRuleProposalResult>.Fail(invalidName);
        }

        var collection = _storageRuntime.Storage.GetCollectionDefinition(request.CollectionName);
        if (collection is null)
        {
            return StorageToolResult<DisplayRuleProposalResult>.Fail(CollectionNotFound(request.CollectionName));
        }

        var rule = DisplayRule.TryCreate(request.Template);
        if (rule is null)
        {
            return StorageToolResult<DisplayRuleProposalResult>.Fail(new StorageToolError(
                "InvalidDisplayRule",
                "template",
                $"Display rule template is required and must be at most {DisplayRule.MaxTemplateLength} characters."));
        }

        var previous = collection.DisplayRule?.Template;
        var validation = _displayRuleValidator.Validate(rule, collection);

        if (!validation.IsValid)
        {
            var errors = validation.Errors.Select(error => error.Message).ToArray();
            _logger.LogWarning(
                "DisplayRule proposal rejected by validation. Collection: {CollectionName}, Template: {DisplayRuleTemplate}, Errors: {ValidationErrors}",
                collection.Name,
                rule.Template,
                string.Join(" | ", errors));

            return StorageToolResult<DisplayRuleProposalResult>.Ok(new DisplayRuleProposalResult(
                collection.Name,
                rule.Template,
                false,
                false,
                false,
                errors,
                null,
                previous));
        }

        // Replacing an existing rule is a change to established behaviour, so it
        // goes to the user unless schema changes are allowed automatically.
        var requiresDecision = !_schemaToolOptions.AllowSchemaChanges;

        if (requiresDecision)
        {
            _logger.LogInformation(
                "DisplayRule proposal awaiting user decision. Collection: {CollectionName}, Template: {DisplayRuleTemplate}",
                collection.Name,
                rule.Template);

            return StorageToolResult<DisplayRuleProposalResult>.Ok(new DisplayRuleProposalResult(
                collection.Name,
                rule.Template,
                true,
                false,
                true,
                Array.Empty<string>(),
                BuildPreview(collection, rule),
                previous));
        }

        try
        {
            _storageRuntime.Storage.SetDisplayRule(collection.Name, rule);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(
                ex,
                "Failed to apply DisplayRule. Collection: {CollectionName}, Template: {DisplayRuleTemplate}",
                collection.Name,
                rule.Template);

            return StorageToolResult<DisplayRuleProposalResult>.Fail(new StorageToolError(
                "DisplayRuleNotApplied", "template", ex.Message));
        }

        _logger.LogInformation(
            "DisplayRule {ChangeKind}. Collection: {CollectionName}, Template: {DisplayRuleTemplate}, PreviousTemplate: {PreviousTemplate}",
            previous is null ? "created" : "changed",
            collection.Name,
            rule.Template,
            previous);

        var applied = _storageRuntime.Storage.GetCollectionDefinition(collection.Name) ?? collection;
        return StorageToolResult<DisplayRuleProposalResult>.Ok(new DisplayRuleProposalResult(
            applied.Name,
            rule.Template,
            true,
            true,
            false,
            Array.Empty<string>(),
            BuildPreview(applied, rule),
            previous));
    }

    /// <summary>
    /// Renders the rule against one existing record so the agent and the user
    /// can see the effect. Returns null when the collection has no records.
    /// </summary>
    private string? BuildPreview(CollectionDefinition collection, DisplayRule rule)
    {
        try
        {
            var sample = _storageRuntime.Storage.GetAll(collection.Name).FirstOrDefault();
            if (sample is null)
            {
                return null;
            }

            return _displayRuleEvaluator.Evaluate(rule, sample.Fields, collection.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not build DisplayRule preview. Collection: {CollectionName}",
                collection.Name);
            return null;
        }
    }

    /// <summary>
    /// Presentation command. Delegates resolution to the domain service, which
    /// validates everything the model supplied; this wrapper only guards against
    /// unexpected failures reaching the chat.
    /// </summary>
    public StorageToolResult<RecordsDisplayMessage> ShowRecords(ShowRecordsRequest request)
    {
        if (request is null)
        {
            return StorageToolResult<RecordsDisplayMessage>.Fail(new StorageToolError(
                "InvalidShowRecordsRequest", "request", "ShowRecords request is required."));
        }

        try
        {
            var message = _recordDisplayService.BuildRecordsDisplay(_storageRuntime.Storage, request);
            return StorageToolResult<RecordsDisplayMessage>.Ok(message);
        }
        catch (Exception ex)
        {
            // Internal details stay in the log, never in the chat.
            _logger.LogError(
                ex,
                "ShowRecords failed unexpectedly. Collection: {CollectionName}, RequestedIds: {RequestedIdCount}",
                request.CollectionName,
                request.RecordIds?.Count ?? 0);

            return StorageToolResult<RecordsDisplayMessage>.Fail(new StorageToolError(
                "ShowRecordsFailed", null, "Records could not be prepared for display."));
        }
    }

    private static StorageToolError CollectionNotFound(string collectionName) =>
        new("CollectionNotFound", "collectionName", $"Collection '{collectionName}' was not found.");
}
