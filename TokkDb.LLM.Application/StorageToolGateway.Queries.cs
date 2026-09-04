using Microsoft.Extensions.Logging;
using TokkDb.LLM.Core;
using TokkDb.LLM.Storage;

namespace TokkDb.LLM.Application;

/// <summary>
/// Declarative record query exposed to agents - the only way records are read.
///
/// The tool model only proxies conditions. This method binds it - turning names
/// into column and relation definitions - and hands the result to storage, which
/// validates that the definitions fit together and runs the query. Errors from
/// either stage come back as tool errors naming the offending column, so the
/// agent can repair the query instead of guessing.
/// </summary>
public sealed partial class StorageToolGateway
{
    public StorageToolResult<RecordQueryResult> QueryRecords(RecordQuery query)
    {
        if (query is null)
        {
            return StorageToolResult<RecordQueryResult>.Fail(new StorageToolError(
                "InvalidRecordQuery", "query", "Query is required."));
        }

        try
        {
            var storage = _storageRuntime.Storage;

            // Names in, definitions out.
            var bound = _recordQueryBinder.Bind(storage, query);

            // Storage validates the bound query and executes it.
            var result = storage.ExecuteQuery(bound);

            _logger.LogInformation(
                "Record query completed. Collection: {CollectionName}, Returned: {ReturnedCount}, Skip: {Skip}, Take: {Take}",
                result.CollectionName,
                result.Rows.Count,
                result.Skip,
                result.Take);

            return StorageToolResult<RecordQueryResult>.Ok(new RecordQueryResult(
                result.CollectionName,
                result.Rows
                    .Select(row => new RecordQueryRow(
                        row.Id.ToString(),
                        row.Fields.ToDictionary(
                            field => field.Key,
                            field => field.Value is null ? null : RecordValueFormatter.Format(field.Value),
                            StringComparer.Ordinal)))
                    .ToArray(),
                result.Skip,
                result.Take,
                result.Rows.Count));
        }
        catch (StorageValidationException ex)
        {
            _logger.LogInformation(
                ex,
                "Record query rejected. Collection: {CollectionName}, Errors: {ValidationErrors}",
                query.CollectionName,
                string.Join(" | ", ex.Errors.Select(error => error.Message)));

            return StorageToolResult<RecordQueryResult>.Fail(ex.Errors.Select(MapStorageError).ToArray());
        }
        catch (Exception ex)
        {
            // Internal detail stays in the log, never in the chat.
            _logger.LogError(
                ex,
                "Record query failed unexpectedly. Collection: {CollectionName}",
                query.CollectionName);

            return StorageToolResult<RecordQueryResult>.Fail(new StorageToolError(
                "RecordQueryFailed", null, "The query could not be executed."));
        }
    }
}
