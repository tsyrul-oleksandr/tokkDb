namespace TokkDb.LLM.Core.Orchestration;

/// <summary>
/// Builds the human-readable outcome text for a document processing operation.
/// Kept in the orchestration layer so the UI only renders text it is given.
/// </summary>
internal static class DocumentOutcomeFormatter
{
    private const int MaxReportedInvalidRecords = 10;

    public static string Build(DocumentProcessingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.State switch
        {
            ProcessingState.Completed => BuildCompleted(context),
            ProcessingState.Failed => $"Document processing failed: {context.FailureReason ?? context.StatusMessage}",
            _ => context.StatusMessage
        };
    }

    private static string BuildCompleted(DocumentProcessingContext context)
    {
        var invalid = context.InvalidRecords.Count;
        if (invalid == 0)
        {
            return $"{context.StatusMessage}\nSaved: {context.SavedRecordCount}. Invalid: 0.";
        }

        var examples = context.InvalidRecords
            .Take(MaxReportedInvalidRecords)
            .Select(record =>
            {
                var errors = string.Join(", ", record.Errors.Select(error => $"{error.Code}:{error.Message}"));
                return $"- {record.CollectionName} row {record.RowNumber}: {errors}";
            });

        return
            $"{context.StatusMessage}\nSaved: {context.SavedRecordCount}. Invalid: {invalid}.\n\nInvalid records:\n" +
            string.Join('\n', examples);
    }
}
